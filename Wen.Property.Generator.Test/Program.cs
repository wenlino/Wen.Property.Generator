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

// object? 版本（值类型会装箱）—— 直接调用，无需扩展方法
Console.WriteLine($"[object?] Name = {c1.GetPropertyValue(nameof(Class1.Name))}");
Console.WriteLine($"[object?] Value = {c1.GetPropertyValue(nameof(Class1.Value))}");

// 强类型泛型版（无装箱损耗）
int idx = c1.GetPropertyValue<int>(nameof(Class1.Index));
Console.WriteLine($"[泛型] Index = {idx}");

c1.SetPropertyValue<string>(nameof(Class1.Name), "new name");
Console.WriteLine($"[泛型 set] Name = {c1.Name}");

// 全能力：ContainsProperty
Console.WriteLine($"[Contains] Value = {c1.ContainsProperty(nameof(Class1.Value))}");
Console.WriteLine($"[Contains] NotExist = {c1.ContainsProperty("NotExist")}");

// 全能力：GetPropertyType
Console.WriteLine($"[Type] CreatedAt = {c1.GetPropertyType(nameof(Class1.CreatedAt))}");

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
