# Wen.Property.Generator

一个基于 **C# Roslyn 增量源生成器** 的零反射属性访问库。通过编译期代码生成，为实现了 `IWenPropertyAccessor` 接口的类自动生成强类型属性读写实现，**无需反射、AOT 友好、零运行时开销**。

## 特性

- **零反射** — 所有属性访问均为编译期生成的直接调用，无 `System.Reflection`
- **AOT 友好** — 无动态代码生成，完全兼容 Native AOT / Trimming
- **增量生成器** — 基于 `IIncrementalGenerator`，仅对变更的类重新生成，编译速度快
- **强类型泛型 API** — 提供 `GetPropertyValue<T>` / `SetPropertyValue<T>` 避免装箱拆箱
- **只读属性保护** — 对只读属性的写操作在运行时抛出明确异常
- **诊断提示** — 未标记 `partial` 的类会触发 `WENPROP001` 编译错误，即时提醒

## 项目结构

```
Wen.Property.Generator.slnx
├── Wen.Property.Generator/          # 源生成器（netstandard2.0, Roslyn Analyzer）
├── Wen.Property.Generator.Runtime/  # 运行时接口库（IWenPropertyAccessor）
└── Wen.Property.Generator.Test/     # 示例 / 测试项目
```

## 安装

通过 NuGet 安装生成器包（会自动携带 Runtime 依赖）：

```bash
dotnet add package Wen.Property.Generator
```

## 快速开始

### 1. 定义类并实现接口

```csharp
using Wen.Property.Generator;

public partial class MyClass : IWenPropertyAccessor
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Remark { get; }  // 只读属性
}
```

> **注意：** 类必须声明为 `partial`，否则触发 `WENPROP001` 编译错误。

### 2. 使用生成的 API

```csharp
var obj = new MyClass { Name = "Alice", Age = 30, CreatedAt = DateTime.Now };

// object? 版本（值类型会装箱）
object? name = obj.GetPropertyValue("Name");
obj.SetPropertyValue("Age", 31);

// 强类型泛型版（无装箱拆箱）
int age = obj.GetPropertyValue<int>("Age");
obj.SetPropertyValue<string>("Name", "Bob");

// 判断属性是否存在
bool exists = obj.ContainsProperty("Name");   // true
bool nope   = obj.ContainsProperty("Foo");    // false

// 获取属性类型
Type t = obj.GetPropertyType("CreatedAt");    // typeof(DateTime)

// 只读属性写入会抛出 ArgumentException
obj.SetPropertyValue("Remark", "x");          // throws!
```

## IWenPropertyAccessor 接口

```csharp
public interface IWenPropertyAccessor
{
    object? GetPropertyValue(string propertyName);
    void SetPropertyValue(string propertyName, object? value);
    TValue GetPropertyValue<TValue>(string propertyName);
    void SetPropertyValue<TValue>(string propertyName, TValue value);
    Type GetPropertyType(string propertyName);
    bool ContainsProperty(string propertyName);
}
```

## 生成规则

| 条件 | 说明 |
|------|------|
| 类必须为 `partial` | 否则报 `WENPROP001` 错误 |
| 仅处理非泛型、非嵌套类 | 泛型类 / 嵌套类会被跳过 |
| 仅包含 `public` 实例属性 | 静态属性、索引器、显式接口实现均被排除 |
| 只读属性 | 生成 getter，setter 抛出 `ArgumentException` |

## 环境要求

- .NET SDK 6.0+（支持 Roslyn 4.x 增量生成器）
- C# 10+
- 目标框架：`netstandard2.0` / `net6.0` / `net8.0` 等均可

## 许可证

MIT