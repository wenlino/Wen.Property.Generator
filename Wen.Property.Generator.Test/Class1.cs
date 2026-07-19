namespace Wen.Property.Generator.Test
{
    [WenGenerateAccessors]
    public class Class1
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
        public double Value { get; set; }
        public bool IsCheck { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? Remark { get; }
    }
}
