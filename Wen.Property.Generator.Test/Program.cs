using Wen.Property.Generator;
using Wen.Property.Generator.Test;

var c1 = new Class1
{
    Name = "000",
    Index = 1,
    Value = 100,
    IsCheck = true,
    CreatedAt = DateTime.Now
};

// object? 版本（值类型会装箱）
Console.WriteLine($"[object?] Name = {c1.GetPropertyValue(nameof(Class1.Name))}");
Console.WriteLine($"[object?] Value = {c1.GetPropertyValue(nameof(Class1.Value))}");

// 强类型泛型版（无装箱损耗）
int idx = c1.GetPropertyValue<Class1, int>(nameof(Class1.Index));
Console.WriteLine($"[泛型] Index = {idx}");

c1.SetPropertyValue<Class1, string>(nameof(Class1.Name), "new name");
Console.WriteLine($"[泛型 set] Name = {c1.Name}");

// 工厂直接调用（修复原 Program.cs 第 12 行缺少 item 参数的 bug）
var accessor = WenPropertyAccessorFactory.GetAccessor<Class1>();
var v2 = accessor.GetPropertyValue(c1, nameof(Class1.Value));
Console.WriteLine($"[工厂] Value = {v2}");

// 全能力：属性名枚举
Console.WriteLine($"[PropertyNames] {string.Join(", ", accessor.PropertyNames)}");

// 全能力：ContainsProperty
Console.WriteLine($"[Contains] Value = {accessor.ContainsProperty(nameof(Class1.Value))}");
Console.WriteLine($"[Contains] NotExist = {accessor.ContainsProperty("NotExist")}");

// 全能力：GetPropertyType
Console.WriteLine($"[Type] CreatedAt = {accessor.GetPropertyType(nameof(Class1.CreatedAt))}");

// 只读属性 set 应抛异常
try
{
    c1.SetPropertyValue(nameof(Class1.Remark), "x");
}
catch (ArgumentException)
{
    Console.WriteLine("[只读拦截] OK");
}

// 不存在的属性应抛异常
try
{
    c1.GetPropertyValue("NotExist");
}
catch (ArgumentException)
{
    Console.WriteLine("[不存在拦截] OK");
}
