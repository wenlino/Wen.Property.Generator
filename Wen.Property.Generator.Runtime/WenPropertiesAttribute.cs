using System;

namespace Wen.Property.Generator
{
    /// <summary>
    /// 声明要为哪些类型生成属性通道表（<c>{类型}Channels</c>）。
    /// <para>挂在程序集上，一处点名、整仓生效：被点名的类型本身不用改一个字——
    /// 不加 <c>partial</c>、不实现接口、不带任何标记，保持 <c>public int Count { get; set; }</c> 这种自动属性。
    /// 生成出来的表是一组 <c>PropertyChannel{TItem,TValue}</c>，读写都是编译期绑好的委托，运行时不按属性名查成员。</para>
    /// <para>列头取属性上的 <c>DisplayNameAttribute</c>，没有就取属性名；要中文表头又不给模型加标记，
    /// 在建列调用点传 <c>header:</c>。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// [assembly: WenProperties(typeof(StockRow), typeof(OrderLine))]
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class WenPropertiesAttribute : Attribute
    {
        /// <summary>要生成通道表的类型，须是引用类型且非泛型。</summary>
        /// <param name="types">目标类型。</param>
        public WenPropertiesAttribute(params Type[] types) => Types = types;

        /// <summary>本条声明点名的类型。</summary>
        public Type[] Types { get; }
    }
}
