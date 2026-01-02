using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nimrita.InstaReload
{
    public static class HotReloadFieldStore
    {
        private static readonly ConditionalWeakTable<object, Dictionary<string, object>> InstanceFields =
            new ConditionalWeakTable<object, Dictionary<string, object>>();
        private static readonly Dictionary<string, object> StaticFields = new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly object StaticLock = new object();

        public static object GetInstanceField(object instance, string fieldKey, Type fieldType)
        {
            if (instance == null)
            {
                return GetDefaultValue(fieldType);
            }

            var fields = InstanceFields.GetOrCreateValue(instance);
            if (!fields.TryGetValue(fieldKey, out var value))
            {
                value = GetDefaultValue(fieldType);
                fields[fieldKey] = value;
            }

            return value;
        }

        public static void SetInstanceField(object instance, string fieldKey, object value)
        {
            if (instance == null)
            {
                return;
            }

            var fields = InstanceFields.GetOrCreateValue(instance);
            fields[fieldKey] = value;
        }

        public static object GetStaticField(string fieldKey, Type fieldType)
        {
            lock (StaticLock)
            {
                if (!StaticFields.TryGetValue(fieldKey, out var value))
                {
                    value = GetDefaultValue(fieldType);
                    StaticFields[fieldKey] = value;
                }

                return value;
            }
        }

        public static void SetStaticField(string fieldKey, object value)
        {
            lock (StaticLock)
            {
                StaticFields[fieldKey] = value;
            }
        }

        private static object GetDefaultValue(Type fieldType)
        {
            if (fieldType == null || !fieldType.IsValueType)
            {
                return null;
            }

            return Activator.CreateInstance(fieldType);
        }
    }
}
