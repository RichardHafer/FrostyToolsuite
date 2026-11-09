using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using FrostySdk;
using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Managers.Entries;
using Newtonsoft.Json.Linq;

namespace FrostyEditor.Mcp
{
    /// <summary>
    /// Writes an EBX asset as a flat list of objects. Pointers stay references so a
    /// single asset cannot pull in the rest of the game.
    /// </summary>
    internal static class EbxMcpSerializer
    {
        public const int DefaultMaxChars = 200000;
        public const int DefaultMaxArrayItems = 64;
        private const int MaxDepth = 32;
        private const int MaxStringLength = 8192;

        private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = new Dictionary<Type, PropertyInfo[]>();

        private sealed class WriteContext
        {
            public AssetManager Assets;
            public int Remaining;
            public int MaxArrayItems;
            public int Depth;
            public bool Truncated;
            public bool BudgetExhausted;
        }

        public static JObject SerializeAsset(EbxAsset asset, EbxAssetEntry entry, AssetManager assets, int maxChars, int maxArrayItems)
        {
            WriteContext ctx = CreateContext(assets, maxChars, maxArrayItems);
            List<object> instances = new List<object>(asset.Objects);
            JArray objects = new JArray();

            foreach (object instance in instances)
            {
                if (ctx.BudgetExhausted && objects.Count > 0)
                {
                    ctx.Truncated = true;
                    break;
                }

                objects.Add(SerializeInstance(instance, ctx));
            }

            if (objects.Count < instances.Count)
                ctx.Truncated = true;

            JObject result = new JObject
            {
                ["name"] = entry.Name,
                ["type"] = entry.Type ?? "",
                ["guid"] = entry.Guid.ToString(),
                ["modified"] = entry.IsModified,
                ["fileGuid"] = asset.FileGuid.ToString(),
                ["objectCount"] = instances.Count,
                ["truncated"] = ctx.Truncated,
                ["objects"] = objects
            };

            try
            {
                result["rootGuid"] = asset.RootInstanceGuid.ToString();
            }
            catch
            {
                // Root instance has no guid on this type.
            }

            return result;
        }

        public static JObject SerializeObject(object instance, EbxAssetEntry entry, AssetManager assets, int maxChars, int maxArrayItems)
        {
            WriteContext ctx = CreateContext(assets, maxChars, maxArrayItems);
            JObject serialized = SerializeInstance(instance, ctx);
            serialized["name"] = entry.Name;
            serialized["assetType"] = entry.Type ?? "";
            serialized["assetGuid"] = entry.Guid.ToString();
            serialized["truncated"] = ctx.Truncated;
            return serialized;
        }

        public static JToken WriteValue(object value)
        {
            WriteContext ctx = CreateContext(null, 500000, 256);
            return SerializeValue(value, ctx);
        }

        public static JToken WriteValue(object value, AssetManager assets, out bool truncated)
        {
            WriteContext ctx = CreateContext(assets, 2000000, 100000);
            JToken token = SerializeValue(value, ctx);
            truncated = ctx.Truncated;
            return token;
        }

        public static bool GuidEquals(object instance, string wanted)
        {
            AssetClassGuid guid;
            if (string.IsNullOrWhiteSpace(wanted) || !TryGetInstanceGuid(instance, out guid))
                return false;

            if (string.Equals(guid.ToString(), wanted.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;

            Guid parsed;
            return guid.IsExported && Guid.TryParse(wanted.Trim(), out parsed) && parsed == guid.ExportedGuid;
        }

        private static WriteContext CreateContext(AssetManager assets, int maxChars, int maxArrayItems)
        {
            if (maxChars < 4096)
                maxChars = DefaultMaxChars;
            if (maxArrayItems < 0)
                maxArrayItems = 0;

            return new WriteContext
            {
                Assets = assets,
                Remaining = maxChars,
                MaxArrayItems = maxArrayItems
            };
        }

        private static JObject SerializeInstance(object instance, WriteContext ctx)
        {
            AssetClassGuid guid;
            bool hasGuid = TryGetInstanceGuid(instance, out guid);
            JObject node = new JObject
            {
                ["type"] = instance.GetType().Name,
                ["exported"] = hasGuid && guid.IsExported,
                ["fields"] = SerializeFields(instance, ctx)
            };

            if (hasGuid)
                node["guid"] = guid.ToString();

            return node;
        }

        private static JObject SerializeFields(object target, WriteContext ctx)
        {
            JObject fields = new JObject();
            if (target == null || ctx.BudgetExhausted)
                return fields;

            ctx.Depth++;
            try
            {
                if (ctx.Depth > MaxDepth)
                {
                    ctx.Truncated = true;
                    return fields;
                }

                foreach (PropertyInfo property in GetProperties(target.GetType()))
                {
                    if (ctx.BudgetExhausted)
                        break;

                    JToken value;
                    try
                    {
                        value = SerializeValue(property.GetValue(target, null), ctx);
                    }
                    catch (Exception ex)
                    {
                        value = new JObject { ["error"] = ex.GetBaseException().Message };
                        Charge(ctx, 32);
                    }

                    fields[property.Name] = value;
                    if (!Charge(ctx, property.Name.Length + 4))
                        break;
                }
            }
            finally
            {
                ctx.Depth--;
            }

            return fields;
        }

        private static JToken SerializeValue(object value, WriteContext ctx)
        {
            if (ctx.BudgetExhausted)
                return TruncatedToken(ctx);

            if (value == null)
                return ChargeValue(ctx, JValue.CreateNull());

            Type type = value.GetType();

            if (type == typeof(string))
                return ChargeValue(ctx, StringToken((string)value, ctx));
            if (type == typeof(bool))
                return ChargeValue(ctx, new JValue((bool)value));
            if (type == typeof(CString))
                return ChargeValue(ctx, StringToken(((CString)value).ToString(), ctx));
            if (type == typeof(FileRef))
                return ChargeValue(ctx, StringToken((string)(FileRef)value, ctx));
            if (type == typeof(ResourceRef))
                return ChargeValue(ctx, new JValue(((ResourceRef)value).ToString()));
            if (type == typeof(TypeRef))
            {
                TypeRef typeRef = (TypeRef)value;
                return ChargeValue(ctx, StringToken(string.IsNullOrEmpty(typeRef.Name) ? typeRef.ToString() : typeRef.Name, ctx));
            }
            if (type == typeof(AssetClassGuid))
                return ChargeValue(ctx, new JValue(value.ToString()));
            if (type == typeof(Guid))
                return ChargeValue(ctx, new JValue(((Guid)value).ToString()));
            if (type.IsEnum)
                return ChargeValue(ctx, new JValue(value.ToString()));

            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort))
                return ChargeValue(ctx, new JValue(Convert.ToInt32(value, CultureInfo.InvariantCulture)));
            if (type == typeof(int))
                return ChargeValue(ctx, IntegerOrHash((int)value));
            if (type == typeof(uint))
            {
                uint unsigned = (uint)value;
                return ChargeValue(ctx, IntegerOrHash(unchecked((int)unsigned), unsigned));
            }
            if (type == typeof(long))
                return ChargeValue(ctx, LongToken((long)value));
            if (type == typeof(ulong))
                return ChargeValue(ctx, new JValue(((ulong)value).ToString(CultureInfo.InvariantCulture)));
            if (type == typeof(float))
                return ChargeValue(ctx, FloatToken((float)value));
            if (type == typeof(double))
                return ChargeValue(ctx, DoubleToken((double)value));

            if (type == typeof(PointerRef))
                return SerializePointer((PointerRef)value, ctx);
            if (type == typeof(BoxedValueRef))
                return SerializeBoxed((BoxedValueRef)value, ctx);
            if (type == typeof(byte[]))
            {
                int length = ((byte[])value).Length;
                ctx.Truncated = ctx.Truncated || length > 0;
                return ChargeValue(ctx, new JObject { ["bytes"] = length });
            }

            IList list = value as IList;
            if (list != null)
                return SerializeList(list, ctx);

            if (ctx.Depth >= MaxDepth)
            {
                ctx.Truncated = true;
                return ChargeValue(ctx, new JObject { ["type"] = type.Name, ["truncated"] = true });
            }

            if (HasFields(type))
                return SerializeFields(value, ctx);

            return ChargeValue(ctx, StringToken(value.ToString(), ctx));
        }

        private static JToken SerializePointer(PointerRef reference, WriteContext ctx)
        {
            if (reference.Type == PointerRefType.Internal)
            {
                if (reference.Internal == null)
                    return ChargeValue(ctx, JValue.CreateNull());

                JObject node = new JObject
                {
                    ["$ref"] = "internal",
                    ["type"] = reference.Internal.GetType().Name
                };
                AssetClassGuid guid;
                if (TryGetInstanceGuid(reference.Internal, out guid))
                    node["guid"] = guid.ToString();
                return ChargeValue(ctx, node);
            }

            if (reference.Type == PointerRefType.External)
            {
                JObject node = new JObject
                {
                    ["$ref"] = "external",
                    ["fileGuid"] = reference.External.FileGuid.ToString(),
                    ["classGuid"] = reference.External.ClassGuid.ToString()
                };

                EbxAssetEntry external = null;
                try
                {
                    if (ctx.Assets != null)
                        external = ctx.Assets.GetEbxEntry(reference.External.FileGuid);
                }
                catch
                {
                    external = null;
                }

                if (external != null)
                    node["asset"] = external.Name;
                return ChargeValue(ctx, node);
            }

            return ChargeValue(ctx, JValue.CreateNull());
        }

        private static JToken SerializeBoxed(BoxedValueRef boxed, WriteContext ctx)
        {
            if (!Charge(ctx, (boxed.TypeString ?? "").Length + 16))
                return TruncatedToken(ctx);

            return new JObject
            {
                ["boxedType"] = boxed.TypeString ?? "",
                ["value"] = SerializeValue(boxed.Value, ctx)
            };
        }

        private static JToken SerializeList(IList list, WriteContext ctx)
        {
            int count = list.Count;
            JArray items = new JArray();
            int take = Math.Min(count, ctx.MaxArrayItems);
            if (take < count)
                ctx.Truncated = true;

            for (int i = 0; i < take; i++)
            {
                if (ctx.BudgetExhausted)
                    break;

                object item;
                try
                {
                    item = list[i];
                }
                catch (Exception ex)
                {
                    items.Add(new JObject { ["error"] = ex.GetBaseException().Message });
                    continue;
                }

                items.Add(SerializeValue(item, ctx));
            }

            if (items.Count < count)
            {
                ctx.Truncated = true;
                return new JObject
                {
                    ["count"] = count,
                    ["truncated"] = true,
                    ["items"] = items
                };
            }

            return items;
        }

        private static JToken IntegerOrHash(int hash)
        {
            string resolved = Utils.GetString(hash);
            if (!string.IsNullOrEmpty(resolved) && !resolved.StartsWith("0x", StringComparison.Ordinal))
            {
                return new JObject
                {
                    ["hash"] = resolved,
                    ["value"] = hash
                };
            }

            return new JValue(hash);
        }

        private static JToken IntegerOrHash(int hash, uint unsigned)
        {
            string resolved = Utils.GetString(hash);
            if (!string.IsNullOrEmpty(resolved) && !resolved.StartsWith("0x", StringComparison.Ordinal))
            {
                return new JObject
                {
                    ["hash"] = resolved,
                    ["value"] = (long)unsigned
                };
            }

            return new JValue((long)unsigned);
        }

        private static JToken LongToken(long value)
        {
            const long jsSafe = 9007199254740991L;
            if (value > jsSafe || value < -jsSafe)
                return new JValue(value.ToString(CultureInfo.InvariantCulture));
            return new JValue(value);
        }

        private static JToken FloatToken(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return new JValue(value.ToString(CultureInfo.InvariantCulture));
            return new JValue(value);
        }

        private static JToken DoubleToken(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return new JValue(value.ToString(CultureInfo.InvariantCulture));
            return new JValue(value);
        }

        private static JToken StringToken(string value, WriteContext ctx)
        {
            if (value == null)
                return JValue.CreateNull();
            if (value.Length > MaxStringLength)
            {
                ctx.Truncated = true;
                return new JValue(value.Substring(0, MaxStringLength));
            }

            return new JValue(value);
        }

        private static JToken ChargeValue(WriteContext ctx, JToken token)
        {
            int cost;
            try
            {
                cost = token.ToString(Newtonsoft.Json.Formatting.None).Length;
            }
            catch
            {
                cost = 32;
            }

            if (!Charge(ctx, cost))
                return TruncatedToken(ctx);
            return token;
        }

        private static JToken TruncatedToken(WriteContext ctx)
        {
            ctx.Truncated = true;
            ctx.BudgetExhausted = true;
            return new JObject { ["truncated"] = true };
        }

        private static bool Charge(WriteContext ctx, int cost)
        {
            if (cost < 0)
                cost = 0;
            if (ctx.BudgetExhausted)
                return false;
            if (cost > ctx.Remaining)
            {
                ctx.Remaining = 0;
                ctx.BudgetExhausted = true;
                ctx.Truncated = true;
                return false;
            }

            ctx.Remaining -= cost;
            return true;
        }

        private static bool HasFields(Type type)
        {
            if (type.Namespace == "FrostySdk.Ebx" && type.BaseType == typeof(Enum))
                return false;
            return GetProperties(type).Length > 0;
        }

        private static PropertyInfo[] GetProperties(Type type)
        {
            PropertyInfo[] cached;
            if (PropertyCache.TryGetValue(type, out cached))
                return cached;

            List<PropertyInfo> properties = new List<PropertyInfo>();
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod == null)
                    continue;
                if (property.Name == "__InstanceGuid")
                    continue;
                if (property.GetCustomAttribute<IsTransientAttribute>(true) != null)
                    continue;
                properties.Add(property);
            }

            properties.Sort(delegate (PropertyInfo left, PropertyInfo right)
            {
                int compare = left.MetadataToken.CompareTo(right.MetadataToken);
                return compare != 0 ? compare : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            });

            PropertyInfo[] result = properties.ToArray();
            PropertyCache[type] = result;
            return result;
        }

        private static bool TryGetInstanceGuid(object obj, out AssetClassGuid guid)
        {
            guid = default(AssetClassGuid);
            if (obj == null)
                return false;

            Type type = obj.GetType();
            MethodInfo method = type.GetMethod("GetInstanceGuid", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method != null)
            {
                object result = method.Invoke(obj, null);
                if (result is AssetClassGuid fromMethod)
                {
                    guid = fromMethod;
                    return true;
                }
            }

            PropertyInfo property = type.GetProperty("__InstanceGuid", BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                object result = property.GetValue(obj, null);
                if (result is AssetClassGuid fromProperty)
                {
                    guid = fromProperty;
                    return true;
                }
            }

            FieldInfo field = type.GetField("__Guid", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? type.GetField("__Guid", BindingFlags.Public | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(AssetClassGuid))
            {
                guid = (AssetClassGuid)field.GetValue(obj);
                return true;
            }

            return false;
        }
    }
}
