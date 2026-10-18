using System;

#if !NETSTANDARD2_0
// required 依赖 RequiredMemberAttribute，netstandard2.0 腿没有这个类型；补一套 polyfill 等于骗编译器，
// 所以通道只做 net8.0/net10.0 两条腿。消费方一律是桌面应用，没有 netstandard 的调用点。
namespace Wen.Property.Generator
{
    /// <summary>
    /// 一条属性的读写通道：把"读写某个业务属性"这件事从运行时的名字查找换成编译期绑定的委托。
    /// <para>为什么要有它：<see cref="System.Linq.Expressions.Expression{TDelegate}"/> 建列看着简洁，
    /// 但树里存的是属性名字符串，建树那一趟就在反射查成员，AOT/裁剪下是笔欠账；
    /// 让行模型实现访问器接口又是把界面的形状塞进业务对象。通道两头都不占——
    /// 读写是 <see cref="Get"/>/<see cref="Set"/> 两个委托，链接期就绑死，行类型保持纯 POCO。</para>
    /// <para>这一份只有数据：名字、表头、两个委托与类型读数，不认任何界面栈。
    /// "值变了怎么通知界面"住在界面那一侧（WenMewUI 按行配槽的 <c>SlotFor</c>/<c>Sync</c> 扩展），
    /// 所以业务程序集为声明通道表只需要引用这个包，不需要把控件库拖进来。</para>
    /// </summary>
    /// <typeparam name="TItem">行数据类型。</typeparam>
    /// <typeparam name="TValue">属性声明类型，可空属性就写可空的那个。</typeparam>
    public sealed class PropertyChannel<TItem, TValue> where TItem : class
    {
        /// <summary>属性名。落盘、按名字找列这类"要把列认出来"的场合用它，不再拿它去查成员。</summary>
        public required string Name { get; init; }

        /// <summary>列头默认文字：属性上的 <c>DisplayName</c>，没有就是属性名。建表时算好，运行时不再读特性。</summary>
        public required string Header { get; init; }

        /// <summary>读这一行的属性现值。</summary>
        public required Func<TItem, TValue> Get { get; init; }

        /// <summary>写回属性。属性没有公开写入口时为 <see langword="null"/>，这样的通道只能建只读列。</summary>
        public Action<TItem, TValue>? Set { get; init; }

        /// <summary>能不能建可编辑列。</summary>
        public bool IsReadOnly => Set is null;

        /// <summary>属性的声明类型，可空属性是 <c>Nullable&lt;T&gt;</c> 那一层。</summary>
        public Type DeclaredType => typeof(TValue);

        /// <summary>展平可空之后的类型：选件、量程、对齐都按它定。</summary>
        public Type UnderlyingType { get; } = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
    }
}
#endif
