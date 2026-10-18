using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Wen.Property.Generator;

[Generator]
public sealed class WenPropertyGenerator : IIncrementalGenerator
{
    private const string InterfaceFullName = "Wen.Property.Generator.IWenPropertyAccessor";
    private const string AttributeFullName = "Wen.Property.Generator.WenPropertiesAttribute";
    private const string ChannelFullName = "WenMewUI.Extensions.PropertyChannel";

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
        messageFormat: "本编译单元里找不到 '{0}'，通道表无处可生成。请引用提供该类型的控件库后再声明 [WenProperties]。",
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

        // 第二条管道：[assembly: WenProperties(typeof(X), …)] 点名的类型出通道表。
        // 走 CompilationProvider 而不是扫语法树——声明在程序集上，且要顺带确认通道类型可达。
        var channelRequests = context.CompilationProvider
            .Select(static (comp, _) => CollectChannelTargets(comp));

        context.RegisterSourceOutput(channelRequests, static (spc, req) =>
        {
            if (req.Targets.Length == 0) return;

            if (req.ChannelMissing)
            {
                spc.ReportDiagnostic(Diagnostic.Create(MissingChannelRule, Location.None, ChannelFullName));
                return;
            }

            var seen = new HashSet<string>();
            foreach (var target in req.Targets)
            {
                if (target is null) continue;
                if (!seen.Add(target.ToDisplayString())) continue;
                GenerateChannelTable(spc, target);
            }
        });
    }

    private readonly struct ChannelRequest
    {
        public ChannelRequest(INamedTypeSymbol?[] targets, bool channelMissing)
        {
            Targets = targets;
            ChannelMissing = channelMissing;
        }

        public INamedTypeSymbol?[] Targets { get; }
        public bool ChannelMissing { get; }
    }

    private static ChannelRequest CollectChannelTargets(Compilation comp)
    {
        var attr = comp.GetTypeByMetadataName(AttributeFullName);
        if (attr is null) return new ChannelRequest(Array.Empty<INamedTypeSymbol?>(), false);

        var channelMissing = comp.GetTypeByMetadataName(ChannelFullName + "`2") is null;

        var targets = new List<INamedTypeSymbol?>();
        foreach (var applied in comp.Assembly.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(applied.AttributeClass, attr)) continue;
            foreach (var argument in applied.ConstructorArguments)
                CollectTypes(argument, targets);
        }

        return new ChannelRequest(targets.ToArray(), channelMissing);
    }

    // params Type[] 交回来是一个数组常量：数组走 Values，单个 Type 实参才是 Value。
    // 直接读数组常量的 Value 会抛 InvalidOperationException。
    private static void CollectTypes(TypedConstant argument, List<INamedTypeSymbol?> targets)
    {
        if (argument.Kind == TypedConstantKind.Array)
        {
            foreach (var item in argument.Values)
                if (item.Value is INamedTypeSymbol inner)
                    targets.Add(CheckTarget(inner));
            return;
        }

        if (argument.Value is INamedTypeSymbol named)
            targets.Add(CheckTarget(named));
    }

    private static INamedTypeSymbol? CheckTarget(INamedTypeSymbol symbol)
    {
        if (symbol.TypeKind != TypeKind.Class) return null;
        return symbol;
    }

    // 生成 {类型}Channels：一条属性一条通道，读写都是编译期绑好的委托。
    private static void GenerateChannelTable(SourceProductionContext context, INamedTypeSymbol symbol)
    {
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
        string name = symbol.Name;
        string ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();
        string accessibility = symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        bool hasNamespace = ns.Length > 0;
        string indent = hasNamespace ? "    " : string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("// 由 Wen.Property.Generator 按 [assembly: WenProperties(…)] 生成。");
        sb.AppendLine("// 行模型本身不用改：这一张表带的就是它的读写委托与列头。");
        sb.AppendLine();

        if (hasNamespace)
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
        }

        sb.AppendLine($"{indent}/// <summary>{name} 的属性通道表（生成）。</summary>");
        sb.AppendLine($"{indent}{accessibility} static class {name}Channels");
        sb.AppendLine($"{indent}{{");
        foreach (var prop in properties)
        {
            // 带可空标注：模型写成 string? 时通道也得是 PropertyChannel<T, string?>，
            // 否则生成的 Get 委托会从可空属性返回非空值，编译期报 CS8603。
            string propType = prop.Type.ToDisplayString(ChannelTypeFormat);
            sb.AppendLine($"{indent}    /// <summary>{prop.Name} 的通道。</summary>");
            sb.AppendLine($"{indent}    public static global::{ChannelFullName}<{itemType}, {propType}> {prop.Name} {{ get; }} = new()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        Name = nameof({itemType}.{prop.Name}),");
            sb.AppendLine($"{indent}        Header = {Literal(DisplayName(prop))},");
            sb.AppendLine($"{indent}        Get = static it => it.{prop.Name},");
            if (!prop.IsReadOnly)
                sb.AppendLine($"{indent}        Set = static (it, value) => it.{prop.Name} = value,");
            sb.AppendLine($"{indent}    }};");
            sb.AppendLine();
        }
        sb.AppendLine($"{indent}}}");

        if (hasNamespace)
        {
            sb.AppendLine("}");
        }

        context.AddSource($"{name}_Channels.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
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
