using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Wen.Property.Generator;

[Generator]
public sealed class WenPropertyGenerator : IIncrementalGenerator
{
    private const string InterfaceFullName = "Wen.Property.Generator.IWenPropertyAccessor";
    private const string AttributeFullName = "Wen.Property.Generator.WenPropertiesAttribute";
    private const string ChannelFullName = "Wen.Property.Generator.PropertyChannel";

    // 全限定 + 保留可空引用标注：通道泛参要跟模型属性声明的可空性一致。
    private static readonly SymbolDisplayFormat ChannelTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly DiagnosticDescriptor MustBePartialRule = new DiagnosticDescriptor(
        id: "WENPROP001",
        title: "实现 IWenPropertyAccessor 的类必须声明为 partial",
        messageFormat: "类型 '{0}' 实现了 IWenPropertyAccessor 接口，但未声明为 partial。请添加 partial 关键字以使源生成器能够生成实现代码。",
        category: "WenPropertyGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingChannelRule = new DiagnosticDescriptor(
        id: "WENPROP002",
        title: "找不到属性通道类型",
        messageFormat: "本编译单元里找不到 '{0}'，通道表无处可生成。请引用提供该类型的 Wen.Property.Generator.Runtime 包后再声明 [WenProperties]。",
        category: "WenPropertyGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoPropertiesRule = new DiagnosticDescriptor(
        id: "WENPROP003",
        title: "目标类型没有可生成的公共实例属性",
        messageFormat: "类型 '{0}' 上没有公共实例属性，生成的通道表会是空的。",
        category: "WenPropertyGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor BadTargetRule = new DiagnosticDescriptor(
        id: "WENPROP004",
        title: "通道表的目标类型不合用",
        messageFormat: "'{0}' 不能生成通道表：要是非泛型的引用类型（通道按行对象配槽，值类型与泛型类型都不成立）。",
        category: "WenPropertyGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor BadCarrierRule = new DiagnosticDescriptor(
        id: "WENPROP005",
        title: "通道表的载体类不合用",
        messageFormat: "载体类 '{0}' 收不下通道表：{1}",
        category: "WenPropertyGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // 通道表的落点：一个挂在载体类上的 [WenProperties(…)] 请求对应一张表。
    // 表恒在载体类里嵌一段与行模型同名的静态类，调用点写 载体类.行模型.属性名。
    private readonly struct ChannelTableRequest
    {
        public ChannelTableRequest(INamedTypeSymbol? target, INamedTypeSymbol carrier, string carrierKeyword, string? carrierProblem)
        {
            Target = target;
            Carrier = carrier;
            CarrierKeyword = carrierKeyword;
            CarrierProblem = carrierProblem;
        }

        public INamedTypeSymbol? Target { get; }
        public INamedTypeSymbol Carrier { get; }
        public string CarrierKeyword { get; }
        public string? CarrierProblem { get; }
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 增量管道：预过滤有基类/接口列表的类声明
        var classDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => node is ClassDeclarationSyntax c && c.BaseList is not null,
            transform: static (ctx, _) => GetClassInfo(ctx))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!.Value)
            .Collect();

        // 注册输出
        context.RegisterSourceOutput(classDeclarations, static (spc, classInfos) =>
        {
            var seen = new HashSet<string>();
            foreach (var info in classInfos)
            {
                if (!seen.Add(info.FullyQualifiedType)) continue;

                if (!info.IsPartial)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(MustBePartialRule, info.Location, info.Name));
                    continue;
                }

                GenerateAccessorCode(spc, info);
            }
        });

        // 第二条管道：载体类点名的行模型出通道表。
        // 通道类型可达与否要问编译本身（WENPROP002），所以左边带一个布尔量进来。
        var declaredTables = context.SyntaxProvider.ForAttributeWithMetadataName(
                AttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, _) => DeclaredTables(ctx))
            .Where(static items => !items.IsEmpty)
            .Collect();

        var channelMissing = context.CompilationProvider
            .Select(static (comp, _) => comp.GetTypeByMetadataName(ChannelFullName + "`2") is null);

        var channelRequests = channelMissing.Combine(declaredTables);

        context.RegisterSourceOutput(channelRequests, static (spc, pair) =>
        {
            var declared = pair.Right;
            if (declared.IsEmpty) return;

            if (pair.Left)
            {
                spc.ReportDiagnostic(Diagnostic.Create(MissingChannelRule, Location.None, ChannelFullName));
                return;
            }

            // 同一个载体类把同一个行模型点两遍只出一张表；点两个不同模型是两张，各自嵌一段。
            var seen = new HashSet<string>();
            var reportedCarriers = new HashSet<string>();
            foreach (var group in declared)
                foreach (var table in group)
                    EmitChannelTable(spc, table, seen, reportedCarriers);
        });
    }

    private static void EmitChannelTable(SourceProductionContext context, ChannelTableRequest table,
        HashSet<string> seen, HashSet<string> reportedCarriers)
    {
        if (table.CarrierProblem is not null)
        {
            // 一个载体类只报一次：它点名两行模型时两条请求共用同一个毛病。
            if (reportedCarriers.Add(table.Carrier.ToDisplayString()))
                context.ReportDiagnostic(Diagnostic.Create(BadCarrierRule, table.Carrier.Locations.FirstOrDefault(),
                    table.Carrier.Name, table.CarrierProblem));
            return;
        }

        if (table.Target is null) return;
        string ownerKey = table.Carrier.ToDisplayString() + "→" + table.Target.ToDisplayString();
        if (!seen.Add(ownerKey)) return;
        GenerateChannelTable(context, table);
    }

    // 类级 [WenProperties(typeof(X), typeof(Y))]：每个点名的行模型在载体类里嵌一段同名静态类，
    // 调用点写 载体类.行模型.属性名——两个模型撞同一个属性名也各自有段落，不会挤进同一层。
    // 载体类要 partial（分段得塞进它的分部声明）、不能点自己（嵌套类不许与外层类型同名）。
    private static ImmutableArray<ChannelTableRequest> DeclaredTables(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol carrier) return ImmutableArray<ChannelTableRequest>.Empty;

        var targets = new List<INamedTypeSymbol>();
        foreach (var applied in context.Attributes)
            foreach (var argument in applied.ConstructorArguments)
                CollectTypes(argument, targets);

        string keyword = carrier.IsRecord ? "record" : "class";
        if (targets.Count == 0)
        {
            return ImmutableArray.Create(new ChannelTableRequest(null, carrier, keyword,
                "没点名行模型。挂在类上得给它点名要建表的行模型类型。"));
        }

        string? problem = null;
        if (targets.Any(target => SymbolEqualityComparer.Default.Equals(target, carrier)))
            problem = "点名的就是它自己：嵌进去的静态分段要和外层类型同名，编译器不认（CS0542）。";
        else if (!IsPartialDeclaration(context.TargetNode)) problem = "不是 partial 声明，通道分段挂不进去；补上 partial 关键字。";
        else if (carrier.IsGenericType) problem = "是泛型类型，通道表要落在闭合类型上。";
        else if (carrier.ContainingType is not null) problem = "是嵌套类型，分部声明得连着外层类型一起写。";
        else
        {
            // 分段名取类型名（不含命名空间）：两个同名模型点进同一个载体类，嵌的两段还是同名，会撞成 CS0102。
            var segments = new HashSet<string>();
            foreach (var target in targets)
                if (!segments.Add(target.Name))
                    problem = "点名的行模型里有同名类型（分属不同命名空间），嵌套分段会撞在载体类上；把它们分到两个载体类。";
        }

        var tables = new List<ChannelTableRequest>(targets.Count);
        foreach (var target in targets)
            tables.Add(new ChannelTableRequest(target, carrier, keyword, problem));
        return [.. tables];
    }

    // partial 只在语法上看得到，符号上没有对应属性。
    private static bool IsPartialDeclaration(SyntaxNode node)
        => node is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword);

    // params Type[] 交回来是一个数组常量：数组走 Values，单个 Type 实参才是 Value。
    // 直接读数组常量的 Value 会抛 InvalidOperationException。
    // 不合用的类型（值类型、泛型）照原样收下来，到生成那趟再报 WENPROP004，别在这儿静默丢掉。
    private static void CollectTypes(TypedConstant argument, List<INamedTypeSymbol> targets)
    {
        if (argument.Kind == TypedConstantKind.Array)
        {
            foreach (var item in argument.Values)
                if (item.Value is INamedTypeSymbol inner) targets.Add(inner);
            return;
        }

        if (argument.Value is INamedTypeSymbol named) targets.Add(named);
    }

    // 生成通道表：一条属性一条通道，读写都是编译期绑好的委托。
    // 载体类里嵌一段与行模型同名的静态类，调用点写 载体类.行模型.属性名。
    private static void GenerateChannelTable(SourceProductionContext context, ChannelTableRequest table)
    {
        var symbol = table.Target!;
        var carrier = table.Carrier;

        // 泛型类无从点名闭合类型；嵌套类的表要落在外层类型名下，命名空间那条取不到，都不合用。
        if (symbol.TypeKind != TypeKind.Class || symbol.IsGenericType || symbol.ContainingType is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(BadTargetRule, symbol.Locations.FirstOrDefault(), symbol.Name));
            return;
        }

        var properties = new List<IPropertySymbol>();
        foreach (var member in symbol.GetMembers())
        {
            if (member is not IPropertySymbol prop) continue;
            if (prop.DeclaredAccessibility != Accessibility.Public) continue;
            if (prop.IsStatic) continue;
            if (prop.IsIndexer) continue;
            if (!prop.ExplicitInterfaceImplementations.IsDefaultOrEmpty) continue;
            properties.Add(prop);
        }

        if (properties.Count == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(NoPropertiesRule, symbol.Locations.FirstOrDefault(), symbol.Name));
            return;
        }

        var itemType = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string ownerName = carrier.Name;
        string ns = carrier.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : carrier.ContainingNamespace.ToDisplayString();
        string accessibility = carrier.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        bool hasNamespace = ns.Length > 0;
        string indent = hasNamespace ? "    " : string.Empty;
        string body = indent + "    ";       // 嵌一段之后的成员缩进层
        string hintName = $"{ownerName}_{symbol.Name}_Channels.g.cs";

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"// 由 Wen.Property.Generator 按挂在 {carrier.Name} 上的 [WenProperties(typeof({symbol.Name}))] 生成。");
        sb.AppendLine("// 行模型本身不用改：这一段带的就是它的读写委托与列头。");
        sb.AppendLine();

        if (hasNamespace)
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
        }

        sb.AppendLine($"{indent}{accessibility} partial {table.CarrierKeyword} {ownerName}");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    /// <summary>{symbol.Name} 的属性通道（生成，调用点写 {ownerName}.{symbol.Name}.属性名）。</summary>");
        sb.AppendLine($"{indent}    public static class {symbol.Name}");
        sb.AppendLine($"{indent}    {{");
        foreach (var prop in properties)
        {
            // 带可空标注：模型写成 string? 时通道也得是 PropertyChannel<T, string?>，
            // 否则生成的 Get 委托会从可空属性返回非空值，编译期报 CS8603。
            string propType = prop.Type.ToDisplayString(ChannelTypeFormat);
            sb.AppendLine($"{body}    /// <summary>{prop.Name} 的通道。</summary>");
            sb.AppendLine($"{body}    public static global::{ChannelFullName}<{itemType}, {propType}> {prop.Name} {{ get; }} = new()");
            sb.AppendLine($"{body}    {{");
            sb.AppendLine($"{body}        Name = nameof({itemType}.{prop.Name}),");
            sb.AppendLine($"{body}        Header = {Literal(DisplayName(prop))},");
            sb.AppendLine($"{body}        Get = static it => it.{prop.Name},");
            if (!prop.IsReadOnly)
                sb.AppendLine($"{body}        Set = static (it, value) => it.{prop.Name} = value,");
            sb.AppendLine($"{body}    }};");
            sb.AppendLine();
        }
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");

        if (hasNamespace)
        {
            sb.AppendLine("}");
        }

        context.AddSource(hintName, SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    // 列头取 DisplayName，没有就取属性名：模型不带标记也能生成，只是表头是英文属性名。
    private static string DisplayName(IPropertySymbol prop)
    {
        foreach (var attribute in prop.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "System.ComponentModel.DisplayNameAttribute") continue;
            if (attribute.ConstructorArguments.FirstOrDefault().Value is string text && text.Length > 0)
                return text;
        }

        return prop.Name;
    }

    private static string Literal(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\0' => "\\0",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString(),
            });
        }

        sb.Append('"');
        return sb.ToString();
    }


    // 提取目标类信息：检查是否直接实现 IWenPropertyAccessor，跳过泛型类与嵌套类
    private static ClassInfo? GetClassInfo(GeneratorSyntaxContext context)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
        if (symbol is null) return null;

        if (symbol.IsGenericType) return null;
        if (symbol.ContainingType is not null) return null;

        // 检查是否直接实现了 IWenPropertyAccessor 接口（不含继承的）
        bool implementsInterface = symbol.Interfaces.Any(i =>
            i.ToDisplayString() == InterfaceFullName);
        if (!implementsInterface) return null;

        string name = symbol.Name;
        string ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();
        string fullyQualifiedType = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        bool isPartial = classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword);
        Location location = classDeclaration.Identifier.GetLocation();

        return new ClassInfo(name, ns, fullyQualifiedType, symbol, isPartial, location);
    }

    // 生成 partial 类的 IWenPropertyAccessor 接口实现
    private static void GenerateAccessorCode(SourceProductionContext context, ClassInfo info)
    {
        // 收集所有有效属性：public、非静态、非索引器、非显式接口实现
        var properties = new List<IPropertySymbol>();
        foreach (var member in info.Symbol.GetMembers())
        {
            if (member is not IPropertySymbol prop) continue;
            if (prop.DeclaredAccessibility != Accessibility.Public) continue;
            if (prop.IsStatic) continue;
            if (prop.IsIndexer) continue;
            if (!prop.ExplicitInterfaceImplementations.IsDefaultOrEmpty) continue;
            properties.Add(prop);
        }

        var sb = new StringBuilder();
        string name = info.Name;
        bool hasNamespace = !string.IsNullOrEmpty(info.Namespace);
        string indent = hasNamespace ? "    " : string.Empty;

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (hasNamespace)
        {
            sb.AppendLine($"namespace {info.Namespace}");
            sb.AppendLine("{");
        }

        // 生成 partial 类，补充 IWenPropertyAccessor 接口方法实现
        sb.AppendLine($"{indent}partial class {name}");
        sb.AppendLine($"{indent}{{");

        // _wenPropertyNames 数组
        sb.AppendLine($"{indent}    private static readonly string[] _wenPropertyNames = new[]");
        sb.AppendLine($"{indent}    {{");
        foreach (var p in properties)
        {
            sb.AppendLine($"{indent}        \"{p.Name}\",");
        }
        sb.AppendLine($"{indent}    }};");
        sb.AppendLine();

        // ContainsProperty
        sb.AppendLine($"{indent}    public bool ContainsProperty(string propertyName) => propertyName switch");
        sb.AppendLine($"{indent}    {{");
        foreach (var p in properties)
        {
            sb.AppendLine($"{indent}        \"{p.Name}\" => true,");
        }
        sb.AppendLine($"{indent}        _ => false");
        sb.AppendLine($"{indent}    }};");
        sb.AppendLine();

        // GetPropertyValue (object? 版本，值类型自动装箱)
        sb.AppendLine($"{indent}    public object? GetPropertyValue(string propertyName) => propertyName switch");
        sb.AppendLine($"{indent}    {{");
        foreach (var p in properties)
        {
            sb.AppendLine($"{indent}        \"{p.Name}\" => this.{p.Name},");
        }
        sb.AppendLine($"{indent}        _ => throw new System.ArgumentException($\"属性 '{{propertyName}}' 在类型 '{name}' 上不存在。\", nameof(propertyName))");
        sb.AppendLine($"{indent}    }};");
        sb.AppendLine();

        // SetPropertyValue (object? 版本)
        sb.AppendLine($"{indent}    public void SetPropertyValue(string propertyName, object? value)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        switch (propertyName)");
        sb.AppendLine($"{indent}        {{");
        foreach (var p in properties)
        {
            if (p.IsReadOnly)
            {
                sb.AppendLine($"{indent}            case \"{p.Name}\": throw new System.ArgumentException($\"属性 '{{propertyName}}' 是只读的，无法设置。\", nameof(propertyName));");
            }
            else
            {
                string ptype = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                sb.AppendLine($"{indent}            case \"{p.Name}\": this.{p.Name} = ({ptype})value!; break;");
            }
        }
        sb.AppendLine($"{indent}            default: throw new System.ArgumentException($\"属性 '{{propertyName}}' 不存在或只读。\", nameof(propertyName));");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        // GetPropertyValue<TValue> (强类型泛型版，无装箱拆箱损耗)
        sb.AppendLine($"{indent}    public TValue GetPropertyValue<TValue>(string propertyName) => propertyName switch");
        sb.AppendLine($"{indent}    {{");
        foreach (var p in properties)
        {
            sb.AppendLine($"{indent}        \"{p.Name}\" => (TValue)(object)this.{p.Name}!,");
        }
        sb.AppendLine($"{indent}        _ => throw new System.ArgumentException($\"属性 '{{propertyName}}' 在类型 '{name}' 上不存在。\", nameof(propertyName))");
        sb.AppendLine($"{indent}    }};");
        sb.AppendLine();

        // SetPropertyValue<TValue> (强类型泛型版)
        sb.AppendLine($"{indent}    public void SetPropertyValue<TValue>(string propertyName, TValue value)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        switch (propertyName)");
        sb.AppendLine($"{indent}        {{");
        foreach (var p in properties)
        {
            if (p.IsReadOnly)
            {
                sb.AppendLine($"{indent}            case \"{p.Name}\": throw new System.ArgumentException($\"属性 '{{propertyName}}' 是只读的，无法设置。\", nameof(propertyName));");
            }
            else
            {
                string ptype = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                sb.AppendLine($"{indent}            case \"{p.Name}\": this.{p.Name} = ({ptype})(object)value!; break;");
            }
        }
        sb.AppendLine($"{indent}            default: throw new System.ArgumentException($\"属性 '{{propertyName}}' 不存在或只读。\", nameof(propertyName));");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();

        // GetPropertyType
        sb.AppendLine($"{indent}    public System.Type GetPropertyType(string propertyName) => propertyName switch");
        sb.AppendLine($"{indent}    {{");
        foreach (var p in properties)
        {
            string ptype = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"{indent}        \"{p.Name}\" => typeof({ptype}),");
        }
        sb.AppendLine($"{indent}        _ => throw new System.ArgumentException($\"属性 '{{propertyName}}' 在类型 '{name}' 上不存在。\", nameof(propertyName))");
        sb.AppendLine($"{indent}    }};");
        sb.AppendLine($"{indent}}}");

        if (hasNamespace)
        {
            sb.AppendLine("}");
        }

        context.AddSource($"{name}_WenAccessors.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    // 普通结构体（netstandard2.0 无 IsExternalInit，不能用 record struct）
    private readonly struct ClassInfo
    {
        public readonly string Name;
        public readonly string Namespace;
        public readonly string FullyQualifiedType;
        public readonly INamedTypeSymbol Symbol;
        public readonly bool IsPartial;
        public readonly Location Location;

        public ClassInfo(string name, string ns, string fullyQualifiedType, INamedTypeSymbol symbol, bool isPartial, Location location)
        {
            Name = name;
            Namespace = ns;
            FullyQualifiedType = fullyQualifiedType;
            Symbol = symbol;
            IsPartial = isPartial;
            Location = location;
        }
    }
}
