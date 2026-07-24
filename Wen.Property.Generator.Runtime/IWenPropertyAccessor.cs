using System.Collections.Generic;

namespace Wen.Property.Generator
{
    /// <summary>
    /// 属性访问器契约。
    /// 目标类需声明为 <c>partial</c> 并实现本接口，
    /// 源生成器将在编译期生成强类型实现，无需反射、无需扩展方法污染。
    /// </summary>
    public interface IWenPropertyAccessor
    {
        /// <summary>获取属性值（object? 版本，值类型会装箱）。</summary>
        object? GetPropertyValue(string propertyName);

        /// <summary>设置属性值（object? 版本）。</summary>
        void SetPropertyValue(string propertyName, object? value);

        /// <summary>获取属性值（强类型泛型版，无装箱拆箱损耗）。</summary>
        TValue GetPropertyValue<TValue>(string propertyName);

        /// <summary>设置属性值（强类型泛型版）。</summary>
        void SetPropertyValue<TValue>(string propertyName, TValue value);

        /// <summary>获取属性的类型。</summary>
        System.Type GetPropertyType(string propertyName);

        /// <summary>判断是否存在指定名称的属性。</summary>
        bool ContainsProperty(string propertyName);
    }
}