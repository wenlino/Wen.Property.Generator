namespace Wen.Property.Generator.Test
{
    public partial class Class1 : IWenPropertyAccessor
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
        public double Value { get; set; }
        public bool IsCheck { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? Remark { get; }
    }
}
