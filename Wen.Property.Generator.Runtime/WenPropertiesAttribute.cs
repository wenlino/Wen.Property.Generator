using System;

namespace Wen.Property.Generator
{
    /// <summary>
    /// 挂在载体类上，声明要为哪些行模型生成属性通道（一组 <c>PropertyChannel{TItem,TValue}</c>）。
    /// <para>被点名的类型本身不用改一个字——不加 <c>partial</c>、不实现接口、不带任何标记，
    /// 保持 <c>public int Count { get; set; }</c> 这种自动属性。读写都是编译期绑好的委托，
    /// 运行时不按属性名查成员。</para>
    /// <para>列头取属性上的 <c>DisplayNameAttribute</c>，没有就取属性名；要中文表头又不给模型加标记，
    /// 在建列调用点传 <c>header:</c>。</para>
    /// <para>一个载体类可以点多个行模型，每个模型在载体类里嵌一段与它同名的静态类，
    /// 调用点写 <c>载体类.行模型.属性名</c>。分段按类型切，所以两个模型撞同一个属性名也各归各段，
    /// 不会挤进同一层。一个模块一个载体，这张表就是这个模块的字段清单。</para>
    /// <para>约束：载体类要 <c>partial</c>（分段得塞进它的分部声明）、不能点它自己
    /// （嵌套段与外层类型同名，编译器不认）、只能落在非泛型的非嵌套类型上。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// [WenProperties(typeof(WorkOrder), typeof(Material))]
    /// public partial class Master;
    /// // → Master.WorkOrder.Qty、Master.Material.Name
    /// //   两个模型都有 Name，各自在自己的段里，互不相干
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class WenPropertiesAttribute : Attribute
    {
        /// <summary>要生成通道表的类型，须是引用类型且非泛型。</summary>
        /// <param name="types">目标类型。</param>
        public WenPropertiesAttribute(params Type[] types) => Types = types;

        /// <summary>本条声明点名的类型。</summary>
        public Type[] Types { get; }
    }
}
