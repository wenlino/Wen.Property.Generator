# Wen.Property.Generator

一个基于 **C# Roslyn 增量源生成器** 的零反射属性访问库。通过编译期代码生成，为属性读写产出强类型委托，
**无需反射、AOT 友好、零运行时开销**。两种触发方式各管一头：

- `[assembly: WenProperties(typeof(X), …)]` → 产出 `{X}Channels` 属性通道表，**目标类不用改一个字**（2.3.0 起，界面用这套）
- 类实现 `IWenPropertyAccessor` → 产出按名字读写的接口实现（老触发保留，业务仓仍在用）

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

## 属性通道表（2.3.0 起，推荐给界面用）

上面那套 `IWenPropertyAccessor` 走的是"按名字查成员"，运行时要一次字符串比较，且要求模型 `partial` + 实现接口。
界面侧（表格列、表单绑定）不需要这个：它只要一对编译期绑好的读写委托。

把类型点名叫出来，模型本身一个字都不用改：

```csharp
// 挂在程序集上，不进模型类
[assembly: WenProperties(typeof(StockRow))]

public class StockRow                      // 不是 partial，不实现接口，不带标记
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public string? Note { get; set; }      // 可空标注会跟着进通道泛型参数
    public decimal Amount { get; }         // 只读属性 → 通道不带 Set
}
```

生成 `StockRowChannels`（与模型同命名空间、同可见性）：

```csharp
public static global::WenMewUI.Extensions.PropertyChannel<StockRow, int> Count { get; } = new()
{
    Name = nameof(StockRow.Count),
    Header = "Count",                      // 有 [DisplayName] 取它，没有就取属性名
    Get = static it => it.Count,
    Set = static (it, value) => it.Count = value,
};
```

委托是 `ldftn` + `newobj`，链接期就绑死了，运行时不查成员名，AOT / trimming 下没有反射路径。
消费现场见 WenMewUI 仓的 `WenMewUI/Extensions/PropertyChannel.cs` 与表格列构建器。

> 中文列头要么在属性上挂 `[DisplayName]`，要么在调用点写 `header:`。
> 只想给一列起中文头、又不愿给模型加标记时，手写这张表也可以——两张表形状一致，可以混用。

## 诊断

| 编号 | 级别 | 触发条件 |
|------|------|----------|
| WENPROP001 | Error | 实现 `IWenPropertyAccessor` 的类没写 `partial` |
| WENPROP002 | Error | `[WenProperties]` 点名了类型，但本编译单元里找不到 `WenMewUI.Extensions.PropertyChannel<TItem, TValue>` |
| WENPROP003 | Warning | 点名的类型没有任何可生成的公共实例属性，通道表为空 |
| WENPROP004 | Error | 点名的类型不合用（非类、泛型类） |

## IWenPropertyAccessor 接口

按名字查成员的旧触发方式，供其他业务仓继续使用，与通道表互不影响。

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

### 通道表（`[assembly: WenProperties(…)]`）

| 条件 | 说明 |
|------|------|
| 目标须为非泛型、非嵌套的类 | 不合用报 `WENPROP004`；结构体等不进点名单 |
| 找不到 `PropertyChannel{TItem, TValue}` | 报 `WENPROP002`，通道表无处可生成 |
| 没有任何公共实例属性 | 报 `WENPROP003` 警告，不产出空表 |
| 仅包含 `public` 实例属性 | 静态属性、索引器、显式接口实现均被排除 |
| 可空引用标注 | `string?` 会原样带进泛型参数，生成的委托不会报 CS8603 |
| 只读属性 | 通道只给 `Get`，`IsReadOnly` 为真，界面据此不挂编辑器 |
| 列头 | 属性上有 `[DisplayName]` 取它，没有则取属性名 |

### 接口实现（`IWenPropertyAccessor`）

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