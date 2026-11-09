using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using FrostySdk;
using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using Newtonsoft.Json.Linq;

namespace FrostyEditor.Mcp
{
    /// <summary>
    /// Edits EBX fields and arrays. Values use the same JSON shape as get_object.
    /// </summary>
    internal static class EbxMcpEdit
    {
        private sealed class IdentityComparer : IEqualityComparer<object>
        {
            public static readonly IdentityComparer Instance = new IdentityComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class Writeback
        {
            public PropertyInfo Property;
            public object Owner;
            public object Box;
            public IList List;
            public int Index;

            public void Apply()
            {
                if (Property != null)
                    Property.SetValue(Owner, Box, null);
                else
                    List[Index] = Box;
            }
        }

        private sealed class Slot
        {
            public object Owner;
            public PropertyInfo Property;
            public IList List;
            public int Index;
            public List<Writeback> Writebacks;

            public Type ValueType => Property != null ? Property.PropertyType : ElementType(List.GetType());

            public object Get()
            {
                return Property != null ? Property.GetValue(Owner, null) : List[Index];
            }

            public void EnsureWritable()
            {
                if (Property == null)
                    return;
                if (!Property.CanWrite || Property.GetCustomAttribute<IsReadOnlyAttribute>() != null)
                    throw new FrostyMcpToolException("Field '" + Property.Name + "' is read only.");
                if (Property.GetCustomAttribute<IsTransientAttribute>(true) != null)
                    throw new FrostyMcpToolException("Field '" + Property.Name + "' is transient and is not saved.");
            }

            public void Set(object value)
            {
                EnsureWritable();
                if (Property != null)
                    Property.SetValue(Owner, value, null);
                else
                    List[Index] = value;
                for (int i = Writebacks.Count - 1; i >= 0; i--)
                    Writebacks[i].Apply();
            }
        }

        private sealed class FieldChange
        {
            public List<object> Path;
            public JToken Value;
            public JToken Expected;
            public bool HasExpected;
            public object Converted;
            public object Previous;
        }

        private sealed class Issue
        {
            public string Severity;
            public string Asset;
            public string Guid;
            public string Path;
            public string Message;
        }

        public static void AddTools(JArray tools)
        {
            tools.Add(FrostyMcpTools.Tool("get_asset_metadata",
                "Get asset metadata, including its EBX type, GUID, object count, modified/dirty status, and availability.",
                new JObject { ["name"] = new JObject { ["type"] = "string" } },
                new[] { "name" }, false));
            tools.Add(FrostyMcpTools.Tool("get_array_slice",
                "Read one slice of an object array without modifying the project. Returns the total array length, the requested items with their absolute indices and full reference data, and nextOffset when more items remain.",
                new JObject
                {
                    ["asset"] = new JObject { ["type"] = "string", ["description"] = "Full EBX asset name." },
                    ["guid"] = new JObject { ["type"] = "string", ["description"] = "GUID of the object that owns the array." },
                    ["path"] = PathSchema("Path to the array, for example [\"Items\"]."),
                    ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Zero-based index of the first item to return." },
                    ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 256, ["default"] = 256, ["description"] = "Maximum number of items to return. Defaults to 256 and cannot exceed 256." }
                },
                new[] { "asset", "guid", "path", "offset" }, false));
            tools.Add(FrostyMcpTools.Tool("get_type_info",
                "Get EBX type information, including fields, value types, required status, defaults, and allowed enum values when available.",
                new JObject
                {
                    ["type"] = new JObject { ["type"] = "string", ["description"] = "EBX class name." },
                    ["includeInheritedFields"] = BoolDefault("Include fields declared on base classes. Defaults to true.", true)
                },
                new[] { "type" }, false));
            tools.Add(FrostyMcpTools.Tool("set_object_field",
                "Set one object field by GUID and path. If expectedValue is provided, verify the current value and reject the operation on conflict.",
                new JObject
                {
                    ["asset"] = new JObject { ["type"] = "string" },
                    ["guid"] = new JObject { ["type"] = "string" },
                    ["path"] = PathSchema("Field path, for example [\"Transform\", \"trans\", \"x\"]."),
                    ["value"] = new JObject(),
                    ["expectedValue"] = new JObject { ["description"] = "Optional expected current value to prevent accidental overwrites." }
                },
                new[] { "asset", "guid", "path", "value" }, false));
            tools.Add(FrostyMcpTools.Tool("set_object_fields",
                "Atomically set multiple fields on one object. If any expectedValue check fails, apply none of the changes.",
                new JObject
                {
                    ["asset"] = new JObject { ["type"] = "string" },
                    ["guid"] = new JObject { ["type"] = "string" },
                    ["changes"] = new JObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["items"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["path"] = PathSchema(null),
                                ["value"] = new JObject(),
                                ["expectedValue"] = new JObject()
                            },
                            ["required"] = new JArray("path", "value"),
                            ["additionalProperties"] = false
                        }
                    }
                },
                new[] { "asset", "guid", "changes" }, false));
            tools.Add(FrostyMcpTools.Tool("insert_array_item",
                "Insert an item into an object array. Validate the value against the field schema, then mark the project as modified.",
                new JObject
                {
                    ["asset"] = new JObject { ["type"] = "string" },
                    ["guid"] = new JObject { ["type"] = "string" },
                    ["path"] = PathSchema("Path to the array."),
                    ["index"] = IndexSchema("Insertion index. An index equal to the array length appends the item."),
                    ["value"] = new JObject()
                },
                new[] { "asset", "guid", "path", "index", "value" }, false));
            tools.Add(FrostyMcpTools.Tool("remove_array_item",
                "Remove one array item by index. If expectedItem is provided, verify it before removal.",
                new JObject
                {
                    ["asset"] = new JObject { ["type"] = "string" },
                    ["guid"] = new JObject { ["type"] = "string" },
                    ["path"] = PathSchema(null),
                    ["index"] = IndexSchema(null),
                    ["expectedItem"] = new JObject()
                },
                new[] { "asset", "guid", "path", "index" }, false));
            tools.Add(FrostyMcpTools.Tool("move_array_item",
                "Move an array item from one index to another without changing the item itself.",
                new JObject
                {
                    ["asset"] = new JObject { ["type"] = "string" },
                    ["guid"] = new JObject { ["type"] = "string" },
                    ["path"] = PathSchema(null),
                    ["fromIndex"] = IndexSchema(null),
                    ["toIndex"] = IndexSchema(null),
                    ["expectedItem"] = new JObject()
                },
                new[] { "asset", "guid", "path", "fromIndex", "toIndex" }, false));
            tools.Add(FrostyMcpTools.Tool("clone_object",
                "Clone an object while preserving its fields and references. Generate a unique GUID and append the clone to an array in the target asset. Do not modify the source object.",
                new JObject
                {
                    ["sourceAsset"] = new JObject { ["type"] = "string" },
                    ["sourceGuid"] = new JObject { ["type"] = "string" },
                    ["targetAsset"] = new JObject { ["type"] = "string" },
                    ["targetArrayPath"] = PathSchema("Path to the array in the target asset where the clone will be added, for example [\"Objects\"]."),
                    ["expectedSourceType"] = new JObject { ["type"] = "string", ["description"] = "Optional expected type of the source object." }
                },
                new[] { "sourceAsset", "sourceGuid", "targetAsset", "targetArrayPath" }, false));
            tools.Add(FrostyMcpTools.Tool("create_object",
                "Create an object of the specified EBX type, validate its fields against the type schema, generate a unique GUID, and add it to an array in the target object.",
                new JObject
                {
                    ["asset"] = new JObject { ["type"] = "string" },
                    ["type"] = new JObject { ["type"] = "string" },
                    ["targetGuid"] = new JObject { ["type"] = "string", ["description"] = "GUID of the object that owns the array where the new object will be added." },
                    ["targetArrayPath"] = PathSchema(null),
                    ["fields"] = new JObject { ["type"] = "object", ["description"] = "Field values for the new object. The server generates its GUID; the client must not reuse one." }
                },
                new[] { "asset", "type", "targetGuid", "targetArrayPath", "fields" }, false));
            tools.Add(FrostyMcpTools.Tool("remove_object",
                "Remove exactly one object with the specified GUID from the given array. If expectedType is provided, verify the type before removal.",
                new JObject
                {
                    ["asset"] = new JObject { ["type"] = "string" },
                    ["guid"] = new JObject { ["type"] = "string" },
                    ["arrayPath"] = PathSchema("Path to the array containing the object."),
                    ["expectedType"] = new JObject { ["type"] = "string" }
                },
                new[] { "asset", "guid", "arrayPath" }, false));
            tools.Add(FrostyMcpTools.Tool("validate_project",
                "Validate the project or selected assets for field types, required fields, duplicate GUIDs, broken references, and errors that may affect saving or exporting. This tool does not modify data.",
                new JObject
                {
                    ["assets"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "string" },
                        ["description"] = "Assets to validate. If omitted, validate the entire project or the current changes."
                    },
                    ["includeWarnings"] = BoolDefault("Include warnings. Defaults to true.", true)
                },
                null, false));
            tools.Add(FrostyMcpTools.Tool("undo_last_change",
                "Undo the most recent change made through the editor or MCP, if supported by Frosty's undo mechanism. Return an error if the change cannot be undone.",
                new JObject
                {
                    ["asset"] = new JObject { ["type"] = "string", ["description"] = "Optional asset used to verify that the expected change is being undone." },
                    ["guid"] = new JObject { ["type"] = "string", ["description"] = "Optional object GUID used for verification." }
                },
                null, false));
        }

        public static bool TryCall(string name, JObject args, out JObject result)
        {
            switch (name)
            {
                case "get_asset_metadata": result = Metadata(args); return true;
                case "get_type_info": result = TypeInfo(args); return true;
                case "get_array_slice": result = ArraySlice(args); return true;
                case "set_object_field": result = SetField(args); return true;
                case "set_object_fields": result = SetFields(args); return true;
                case "insert_array_item": result = InsertItem(args); return true;
                case "remove_array_item": result = RemoveItem(args); return true;
                case "move_array_item": result = MoveItem(args); return true;
                case "clone_object": result = CloneObject(args); return true;
                case "create_object": result = CreateObject(args); return true;
                case "remove_object": result = RemoveObject(args); return true;
                case "validate_project": result = Validate(args); return true;
                case "undo_last_change": result = Undo(args); return true;
                default:
                    result = null;
                    return false;
            }
        }

        private static JObject Metadata(JObject args)
        {
            if (!Ready())
                return Idle();
            string name = FrostyMcpTools.RequiredString(args, "name");
            return FrostyMcpTools.OnUi(delegate { return MetadataOnUi(name); });
        }

        private static JObject MetadataOnUi(string name)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null)
                return new JObject { ["name"] = name, ["available"] = false };

            JObject result = new JObject
            {
                ["name"] = entry.Name,
                ["available"] = true,
                ["type"] = entry.Type ?? "",
                ["guid"] = entry.Guid.ToString(),
                ["modified"] = entry.IsModified,
                ["dirty"] = entry.IsDirty
            };

            try
            {
                EbxAsset asset = FrostyMcpTools.LoadEditable(entry);
                int count = 0;
                foreach (object unused in asset.Objects)
                    count++;
                result["objectCount"] = count;
            }
            catch (Exception ex)
            {
                result["message"] = ex.GetBaseException().Message;
            }

            return result;
        }

        private static JObject ArraySlice(JObject args)
        {
            if (!Ready())
                return Idle();

            string assetName = FrostyMcpTools.RequiredString(args, "asset");
            string guid = FrostyMcpTools.RequiredString(args, "guid");
            List<object> path = ReadPath(args["path"], "path");
            int offset = RequiredIndex(args, "offset");
            int limit = SliceLimit(args);

            EbxAssetEntry entry = FrostyMcpTools.RequireEntry(assetName);
            EbxAsset asset = FrostyMcpTools.ReadAsset(entry);
            object target = RequireObject(asset, guid);
            PropertyInfo property;
            List<Writeback> writebacks;
            IList list = OpenList(target, path, out property, out writebacks);

            if (offset > list.Count)
                throw new FrostyMcpToolException("Offset " + offset + " is past the end of an array of " + list.Count + ".");

            int take = Math.Min(limit, list.Count - offset);
            JArray items = new JArray();
            for (int i = 0; i < take; i++)
            {
                int index = offset + i;
                bool truncated;
                JToken value;
                try
                {
                    value = EbxMcpSerializer.WriteValue(list[index], App.AssetManager, out truncated);
                }
                catch (Exception ex)
                {
                    value = new JObject { ["error"] = ex.GetBaseException().Message };
                    truncated = false;
                }

                JObject item = new JObject
                {
                    ["index"] = index,
                    ["value"] = value
                };
                if (truncated)
                    item["truncated"] = true;
                items.Add(item);
            }

            JObject result = new JObject
            {
                ["asset"] = entry.Name,
                ["guid"] = guid,
                ["path"] = PathArray(path),
                ["count"] = list.Count,
                ["offset"] = offset,
                ["items"] = items
            };
            if (offset + take < list.Count)
                result["nextOffset"] = offset + take;
            return result;
        }

        private static int SliceLimit(JObject args)
        {
            JToken token = args?["limit"];
            if (token == null || token.Type == JTokenType.Null)
                return 256;
            if (token.Type != JTokenType.Integer)
                throw new FrostyMcpToolException("'limit' must be an integer.");

            long value = token.Value<long>();
            if (value < 1 || value > 256)
                throw new FrostyMcpToolException("'limit' must be from 1 to 256.");
            return (int)value;
        }

        private static JObject TypeInfo(JObject args)
        {
            if (!Ready())
                return Idle();

            string name = FrostyMcpTools.RequiredString(args, "type");
            bool inherited = FrostyMcpTools.ArgBool(args, "includeInheritedFields", true);
            Type type = TypeLibrary.GetType(name);
            if (type == null)
                throw new FrostyMcpToolException("EBX type '" + name + "' was not found.");

            JObject result = new JObject
            {
                ["type"] = type.Name,
                ["includeInheritedFields"] = inherited
            };
            if (type.BaseType != null && type.BaseType != typeof(object))
                result["baseType"] = type.BaseType.Name;

            EbxClassMetaAttribute meta = type.GetCustomAttribute<EbxClassMetaAttribute>();
            if (meta != null)
            {
                result["ebxType"] = meta.Type.ToString();
                if (!string.IsNullOrEmpty(meta.Namespace))
                    result["namespace"] = meta.Namespace;
            }

            if (type.IsEnum)
            {
                result["kind"] = "enum";
                result["enumValues"] = Names(type);
                result["fields"] = new JArray();
                return result;
            }

            result["kind"] = "class";
            object prototype = null;
            try
            {
                prototype = TypeLibrary.CreateObject(type.Name);
            }
            catch
            {
                prototype = null;
            }

            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            if (!inherited)
                flags |= BindingFlags.DeclaredOnly;

            List<PropertyInfo> properties = new List<PropertyInfo>();
            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod == null || property.Name.StartsWith("__", StringComparison.Ordinal))
                    continue;
                properties.Add(property);
            }
            properties.Sort(delegate (PropertyInfo left, PropertyInfo right) { return left.MetadataToken.CompareTo(right.MetadataToken); });

            JArray fields = new JArray();
            foreach (PropertyInfo property in properties)
                fields.Add(DescribeField(property, prototype));
            result["fields"] = fields;
            return result;
        }

        private static JObject DescribeField(PropertyInfo property, object prototype)
        {
            EbxFieldMetaAttribute meta = property.GetCustomAttribute<EbxFieldMetaAttribute>(true);
            Type element = ElementType(property.PropertyType);
            JObject field = new JObject
            {
                ["name"] = property.Name,
                ["type"] = property.PropertyType.Name,
                ["valueType"] = ValueKind(property.PropertyType, meta),
                ["required"] = IsRequired(property, meta),
                ["readOnly"] = !property.CanWrite || property.GetCustomAttribute<IsReadOnlyAttribute>() != null,
                ["reference"] = property.GetCustomAttribute<IsReferenceAttribute>() != null
            };
            if (property.GetCustomAttribute<IsTransientAttribute>(true) != null)
                field["transient"] = true;
            if (meta != null)
            {
                field["ebxType"] = meta.Type.ToString();
                field["isArray"] = meta.IsArray;
                if (meta.IsArray)
                    field["arrayType"] = meta.ArrayType.ToString();
                if (meta.BaseType != null && meta.BaseType != typeof(object))
                    field["baseType"] = meta.BaseType.Name;
            }
            else if (typeof(IList).IsAssignableFrom(property.PropertyType) && property.PropertyType != typeof(string))
            {
                field["isArray"] = true;
            }

            DescriptionAttribute description = property.GetCustomAttribute<DescriptionAttribute>();
            if (description != null && !string.IsNullOrEmpty(description.Description))
                field["description"] = description.Description;
            CategoryAttribute category = property.GetCustomAttribute<CategoryAttribute>();
            if (category != null && !string.IsNullOrEmpty(category.Name))
                field["category"] = category.Name;

            Type enumType = property.PropertyType.IsEnum ? property.PropertyType : (element != null && element.IsEnum ? element : null);
            if (enumType != null)
                field["enumValues"] = Names(enumType);

            if (prototype != null && property.GetMethod != null)
            {
                try
                {
                    field["default"] = EbxMcpSerializer.WriteValue(property.GetValue(prototype, null));
                }
                catch
                {
                    // This field has no readable default.
                }
            }

            return field;
        }

        private static JObject SetField(JObject args)
        {
            if (!Ready())
                return Idle();
            string asset = FrostyMcpTools.RequiredString(args, "asset");
            string guid = FrostyMcpTools.RequiredString(args, "guid");
            List<object> path = ReadPath(args["path"], "path");
            if (args.Property("value") == null)
                throw new FrostyMcpToolException("'value' is required.");
            FieldChange change = new FieldChange
            {
                Path = path,
                Value = args["value"],
                Expected = args["expectedValue"],
                HasExpected = args.Property("expectedValue") != null
            };
            return FrostyMcpTools.OnUi(delegate { return SetFieldsOnUi(asset, guid, new List<FieldChange> { change }, true); });
        }

        private static JObject SetFields(JObject args)
        {
            if (!Ready())
                return Idle();
            string asset = FrostyMcpTools.RequiredString(args, "asset");
            string guid = FrostyMcpTools.RequiredString(args, "guid");
            JArray items = args["changes"] as JArray;
            if (items == null || items.Count == 0)
                throw new FrostyMcpToolException("'changes' must contain at least one change.");

            List<FieldChange> changes = new List<FieldChange>();
            for (int i = 0; i < items.Count; i++)
            {
                JObject item = items[i] as JObject;
                if (item == null)
                    throw new FrostyMcpToolException("Change " + i + " must be an object.");
                if (item.Property("value") == null)
                    throw new FrostyMcpToolException("Change " + i + " is missing 'value'.");
                changes.Add(new FieldChange
                {
                    Path = ReadPath(item["path"], "changes[" + i + "].path"),
                    Value = item["value"],
                    Expected = item["expectedValue"],
                    HasExpected = item.Property("expectedValue") != null
                });
            }

            return FrostyMcpTools.OnUi(delegate { return SetFieldsOnUi(asset, guid, changes, false); });
        }

        private static JObject SetFieldsOnUi(string assetName, string guid, List<FieldChange> changes, bool single)
        {
            EbxAssetEntry entry = FrostyMcpTools.RequireEntry(assetName);
            EbxAsset asset = FrostyMcpTools.LoadEditable(entry);
            object target = RequireObject(asset, guid);

            foreach (FieldChange change in changes)
            {
                Slot slot = Resolve(target, change.Path);
                slot.EnsureWritable();
                object current = slot.Get();
                if (change.HasExpected)
                    Expect(current, change.Expected, slot.ValueType, asset, entry, slot.Property, FormatPath(change.Path));
                change.Converted = ConvertValue(change.Value, slot.ValueType ?? current?.GetType() ?? typeof(object), asset, entry, current, slot.Property);
            }

            List<FieldChange> applied = new List<FieldChange>();
            try
            {
                foreach (FieldChange change in changes)
                {
                    Slot slot = Resolve(target, change.Path);
                    change.Previous = slot.Get();
                    slot.Set(change.Converted);
                    applied.Add(change);
                }
            }
            catch (Exception ex)
            {
                Rollback(target, applied, ex);
                throw;
            }

            FrostyMcpTools.CommitAsset(asset, entry);
            EbxMcpUndo.Push(entry.Name, guid, "Set " + applied.Count + " field" + (applied.Count == 1 ? "" : "s") + " on " + guid, delegate
            {
                EbxAsset live = FrostyMcpTools.LoadEditable(entry);
                object liveTarget = RequireObject(live, guid);
                for (int i = applied.Count - 1; i >= 0; i--)
                    Resolve(liveTarget, applied[i].Path).Set(applied[i].Previous);
                FrostyMcpTools.CommitAsset(live, entry);
            });

            if (single)
            {
                return new JObject
                {
                    ["asset"] = entry.Name,
                    ["guid"] = guid,
                    ["path"] = PathArray(changes[0].Path),
                    ["previous"] = PreviewToken(changes[0].Previous),
                    ["value"] = PreviewToken(Resolve(target, changes[0].Path).Get()),
                    ["dirty"] = true
                };
            }

            return new JObject
            {
                ["asset"] = entry.Name,
                ["guid"] = guid,
                ["applied"] = applied.Count,
                ["dirty"] = true
            };
        }

        private static void Rollback(object target, List<FieldChange> applied, Exception failure)
        {
            for (int i = applied.Count - 1; i >= 0; i--)
            {
                try
                {
                    Resolve(target, applied[i].Path).Set(applied[i].Previous);
                }
                catch (Exception rollback)
                {
                    throw new FrostyMcpToolException(failure.GetBaseException().Message + " The edit could not be rolled back: " + rollback.GetBaseException().Message);
                }
            }
        }

        private static JObject InsertItem(JObject args)
        {
            if (!Ready())
                return Idle();
            string asset = FrostyMcpTools.RequiredString(args, "asset");
            string guid = FrostyMcpTools.RequiredString(args, "guid");
            List<object> path = ReadPath(args["path"], "path");
            int index = RequiredIndex(args, "index");
            if (args.Property("value") == null)
                throw new FrostyMcpToolException("'value' is required.");
            JToken value = args["value"];
            return FrostyMcpTools.OnUi(delegate { return InsertOnUi(asset, guid, path, index, value); });
        }

        private static JObject InsertOnUi(string assetName, string guid, List<object> path, int index, JToken value)
        {
            EbxAssetEntry entry = FrostyMcpTools.RequireEntry(assetName);
            EbxAsset asset = FrostyMcpTools.LoadEditable(entry);
            object target = RequireObject(asset, guid);
            PropertyInfo property;
            List<Writeback> writebacks;
            IList list = OpenList(target, path, out property, out writebacks);
            EnsureResizable(list);
            if (index > list.Count)
                throw new FrostyMcpToolException("Index " + index + " is past the end of an array of " + list.Count + ".");

            Type element = ElementType(list.GetType());
            if (element == null && property != null)
                element = ElementType(property.PropertyType);
            if (element == null)
                element = typeof(object);
            object converted = ConvertValue(value, element, asset, entry, null, property);
            try
            {
                list.Insert(index, converted);
            }
            catch (NotSupportedException)
            {
                throw new FrostyMcpToolException("The array cannot be resized.");
            }
            Finish(writebacks);
            FrostyMcpTools.CommitAsset(asset, entry);

            object inserted = converted;
            EbxMcpUndo.Push(entry.Name, guid, "Insert array item at " + index + " on " + guid, delegate
            {
                EbxAsset live = FrostyMcpTools.LoadEditable(entry);
                object liveTarget = RequireObject(live, guid);
                PropertyInfo ignored;
                List<Writeback> liveWrites;
                IList liveList = OpenList(liveTarget, path, out ignored, out liveWrites);
                if (index >= liveList.Count || !Same(liveList[index], inserted, 0))
                    throw new FrostyMcpToolException("The array changed and the insertion cannot be undone.");
                liveList.RemoveAt(index);
                Finish(liveWrites);
                FrostyMcpTools.CommitAsset(live, entry);
            });

            return new JObject
            {
                ["asset"] = entry.Name,
                ["guid"] = guid,
                ["path"] = PathArray(path),
                ["index"] = index,
                ["count"] = list.Count,
                ["dirty"] = true
            };
        }

        private static JObject RemoveItem(JObject args)
        {
            if (!Ready())
                return Idle();
            string asset = FrostyMcpTools.RequiredString(args, "asset");
            string guid = FrostyMcpTools.RequiredString(args, "guid");
            List<object> path = ReadPath(args["path"], "path");
            int index = RequiredIndex(args, "index");
            bool hasExpected = args.Property("expectedItem") != null;
            JToken expected = args["expectedItem"];
            return FrostyMcpTools.OnUi(delegate { return RemoveItemOnUi(asset, guid, path, index, hasExpected, expected); });
        }

        private static JObject RemoveItemOnUi(string assetName, string guid, List<object> path, int index, bool hasExpected, JToken expected)
        {
            EbxAssetEntry entry = FrostyMcpTools.RequireEntry(assetName);
            EbxAsset asset = FrostyMcpTools.LoadEditable(entry);
            object target = RequireObject(asset, guid);
            PropertyInfo property;
            List<Writeback> writebacks;
            IList list = OpenList(target, path, out property, out writebacks);
            EnsureResizable(list);
            if (index >= list.Count)
                throw new FrostyMcpToolException("Index " + index + " is past the end of an array of " + list.Count + ".");
            object current = list[index];
            if (hasExpected)
                Expect(current, expected, current?.GetType() ?? ElementType(list.GetType()) ?? typeof(object), asset, entry, property, FormatPath(path) + "[" + index + "]");

            object removed = current;
            list.RemoveAt(index);
            Finish(writebacks);
            FrostyMcpTools.CommitAsset(asset, entry);
            EbxMcpUndo.Push(entry.Name, guid, "Remove array item " + index + " on " + guid, delegate
            {
                EbxAsset live = FrostyMcpTools.LoadEditable(entry);
                object liveTarget = RequireObject(live, guid);
                PropertyInfo ignored;
                List<Writeback> liveWrites;
                IList liveList = OpenList(liveTarget, path, out ignored, out liveWrites);
                EnsureResizable(liveList);
                int insertAt = index > liveList.Count ? liveList.Count : index;
                liveList.Insert(insertAt, removed);
                Finish(liveWrites);
                FrostyMcpTools.CommitAsset(live, entry);
            });

            return new JObject
            {
                ["asset"] = entry.Name,
                ["guid"] = guid,
                ["path"] = PathArray(path),
                ["index"] = index,
                ["count"] = list.Count,
                ["dirty"] = true
            };
        }

        private static JObject MoveItem(JObject args)
        {
            if (!Ready())
                return Idle();
            string asset = FrostyMcpTools.RequiredString(args, "asset");
            string guid = FrostyMcpTools.RequiredString(args, "guid");
            List<object> path = ReadPath(args["path"], "path");
            int fromIndex = RequiredIndex(args, "fromIndex");
            int toIndex = RequiredIndex(args, "toIndex");
            bool hasExpected = args.Property("expectedItem") != null;
            JToken expected = args["expectedItem"];
            return FrostyMcpTools.OnUi(delegate { return MoveOnUi(asset, guid, path, fromIndex, toIndex, hasExpected, expected); });
        }

        private static JObject MoveOnUi(string assetName, string guid, List<object> path, int fromIndex, int toIndex, bool hasExpected, JToken expected)
        {
            EbxAssetEntry entry = FrostyMcpTools.RequireEntry(assetName);
            EbxAsset asset = FrostyMcpTools.LoadEditable(entry);
            object target = RequireObject(asset, guid);
            PropertyInfo property;
            List<Writeback> writebacks;
            IList list = OpenList(target, path, out property, out writebacks);
            if (fromIndex >= list.Count || toIndex >= list.Count)
                throw new FrostyMcpToolException("The array has " + list.Count + " items.");
            object current = list[fromIndex];
            if (hasExpected)
                Expect(current, expected, current?.GetType() ?? typeof(object), asset, entry, property, FormatPath(path) + "[" + fromIndex + "]");
            if (fromIndex != toIndex)
            {
                list.RemoveAt(fromIndex);
                list.Insert(toIndex, current);
                Finish(writebacks);
                FrostyMcpTools.CommitAsset(asset, entry);
                EbxMcpUndo.Push(entry.Name, guid, "Move array item " + fromIndex + " to " + toIndex + " on " + guid, delegate
                {
                    EbxAsset live = FrostyMcpTools.LoadEditable(entry);
                    object liveTarget = RequireObject(live, guid);
                    PropertyInfo ignored;
                    List<Writeback> liveWrites;
                    IList liveList = OpenList(liveTarget, path, out ignored, out liveWrites);
                    if (toIndex >= liveList.Count || fromIndex >= liveList.Count)
                        throw new FrostyMcpToolException("The array changed and the move cannot be undone.");
                    object item = liveList[toIndex];
                    liveList.RemoveAt(toIndex);
                    liveList.Insert(fromIndex, item);
                    Finish(liveWrites);
                    FrostyMcpTools.CommitAsset(live, entry);
                });
            }

            return new JObject
            {
                ["asset"] = entry.Name,
                ["guid"] = guid,
                ["path"] = PathArray(path),
                ["fromIndex"] = fromIndex,
                ["toIndex"] = toIndex,
                ["dirty"] = fromIndex != toIndex
            };
        }

        private static JObject CloneObject(JObject args)
        {
            if (!Ready())
                return Idle();
            string sourceAsset = FrostyMcpTools.RequiredString(args, "sourceAsset");
            string sourceGuid = FrostyMcpTools.RequiredString(args, "sourceGuid");
            string targetAsset = FrostyMcpTools.RequiredString(args, "targetAsset");
            List<object> path = ReadPath(args["targetArrayPath"], "targetArrayPath");
            string expectedType = FrostyMcpTools.ArgString(args, "expectedSourceType");
            return FrostyMcpTools.OnUi(delegate { return CloneOnUi(sourceAsset, sourceGuid, targetAsset, path, expectedType); });
        }

        private static JObject CloneOnUi(string sourceName, string sourceGuid, string targetName, List<object> path, string expectedType)
        {
            EbxAssetEntry sourceEntry = FrostyMcpTools.RequireEntry(sourceName);
            EbxAssetEntry targetEntry = FrostyMcpTools.RequireEntry(targetName);
            EbxAsset sourceAsset = FrostyMcpTools.LoadEditable(sourceEntry);
            EbxAsset targetAsset = ReferenceEquals(sourceEntry, targetEntry) ? sourceAsset : FrostyMcpTools.LoadEditable(targetEntry);
            object source = RequireObject(sourceAsset, sourceGuid);
            if (!string.IsNullOrWhiteSpace(expectedType) && !TypeMatches(source, expectedType))
                throw new FrostyMcpToolException("Source object is " + source.GetType().Name + ", not " + expectedType + ".");

            object clone = EbxObjectCloner.ClonePreservingSharedReferences(source, sourceAsset, sourceEntry, targetAsset, targetEntry);
            PropertyInfo property;
            List<Writeback> writebacks;
            IList list = OpenList(targetAsset.RootObject, path, out property, out writebacks);
            EnsureResizable(list);
            object stored = BoxForList(clone, ElementType(list.GetType()) ?? (property != null ? ElementType(property.PropertyType) : null) ?? typeof(object));
            list.Add(stored);
            Finish(writebacks);
            FrostyMcpTools.CommitAsset(targetAsset, targetEntry);

            string newGuid = ReadGuid(clone);
            EbxMcpUndo.Push(targetEntry.Name, newGuid, sourceGuid, "Clone object " + sourceGuid + " into " + targetEntry.Name, delegate
            {
                EbxAsset live = FrostyMcpTools.LoadEditable(targetEntry);
                PropertyInfo ignored;
                List<Writeback> liveWrites;
                IList liveList = OpenList(live.RootObject, path, out ignored, out liveWrites);
                RemoveStored(liveList, clone);
                live.RemoveObject(clone);
                Finish(liveWrites);
                FrostyMcpTools.CommitAsset(live, targetEntry);
            });

            return new JObject
            {
                ["sourceAsset"] = sourceEntry.Name,
                ["targetAsset"] = targetEntry.Name,
                ["guid"] = newGuid ?? "",
                ["type"] = clone.GetType().Name,
                ["dirty"] = true
            };
        }

        private static JObject CreateObject(JObject args)
        {
            if (!Ready())
                return Idle();
            string assetName = FrostyMcpTools.RequiredString(args, "asset");
            string typeName = FrostyMcpTools.RequiredString(args, "type");
            string targetGuid = FrostyMcpTools.RequiredString(args, "targetGuid");
            List<object> path = ReadPath(args["targetArrayPath"], "targetArrayPath");
            JObject fields = args["fields"] as JObject;
            if (fields == null)
                throw new FrostyMcpToolException("'fields' must be an object.");
            return FrostyMcpTools.OnUi(delegate { return CreateOnUi(assetName, typeName, targetGuid, path, fields); });
        }

        private static JObject CreateOnUi(string assetName, string typeName, string targetGuid, List<object> path, JObject fields)
        {
            Type type = TypeLibrary.GetType(typeName);
            if (type == null)
                throw new FrostyMcpToolException("EBX type '" + typeName + "' was not found.");

            EbxAssetEntry entry = FrostyMcpTools.RequireEntry(assetName);
            EbxAsset asset = FrostyMcpTools.LoadEditable(entry);
            object owner = RequireObject(asset, targetGuid);
            object instance = TypeLibrary.CreateObject(type.Name) ?? Activator.CreateInstance(type);
            string guid = AssignGuid(instance, asset, entry);
            ApplyFields(instance, fields, asset, entry);
            ApplyPeerFlags(instance, fields, guid);

            bool added = false;
            try
            {
                if (IsPointerClass(instance.GetType()))
                {
                    asset.AddObject(instance);
                    added = true;
                }

                PropertyInfo property;
                List<Writeback> writebacks;
                IList list = OpenList(owner, path, out property, out writebacks);
                EnsureResizable(list);
                Type element = ElementType(list.GetType()) ?? (property != null ? ElementType(property.PropertyType) : null) ?? typeof(object);
                list.Add(BoxForList(instance, element));
                Finish(writebacks);
                FrostyMcpTools.CommitAsset(asset, entry);
            }
            catch
            {
                if (added)
                    asset.RemoveObject(instance);
                throw;
            }

            EbxMcpUndo.Push(entry.Name, guid, "Create " + instance.GetType().Name + " " + (guid ?? "") , delegate
            {
                EbxAsset live = FrostyMcpTools.LoadEditable(entry);
                object liveOwner = RequireObject(live, targetGuid);
                PropertyInfo ignored;
                List<Writeback> liveWrites;
                IList liveList = OpenList(liveOwner, path, out ignored, out liveWrites);
                RemoveStored(liveList, instance);
                live.RemoveObject(instance);
                Finish(liveWrites);
                FrostyMcpTools.CommitAsset(live, entry);
            });

            JObject result = new JObject
            {
                ["asset"] = entry.Name,
                ["type"] = instance.GetType().Name,
                ["dirty"] = true
            };
            if (!string.IsNullOrEmpty(guid))
                result["guid"] = guid;
            return result;
        }

        private static JObject RemoveObject(JObject args)
        {
            if (!Ready())
                return Idle();
            string assetName = FrostyMcpTools.RequiredString(args, "asset");
            string guid = FrostyMcpTools.RequiredString(args, "guid");
            List<object> path = ReadPath(args["arrayPath"], "arrayPath");
            string expectedType = FrostyMcpTools.ArgString(args, "expectedType");
            return FrostyMcpTools.OnUi(delegate { return RemoveObjectOnUi(assetName, guid, path, expectedType); });
        }

        private static JObject RemoveObjectOnUi(string assetName, string guid, List<object> path, string expectedType)
        {
            EbxAssetEntry entry = FrostyMcpTools.RequireEntry(assetName);
            EbxAsset asset = FrostyMcpTools.LoadEditable(entry);
            PropertyInfo property;
            List<Writeback> writebacks;
            IList list = OpenList(asset.RootObject, path, out property, out writebacks);
            int found = -1;
            object stored = null;
            object instance = null;
            for (int i = 0; i < list.Count; i++)
            {
                object candidate = Unwrap(list[i]);
                if (candidate == null || !EbxMcpSerializer.GuidEquals(candidate, guid))
                    continue;
                if (found != -1)
                    throw new FrostyMcpToolException("More than one array entry matches '" + guid + "'.");
                found = i;
                stored = list[i];
                instance = candidate;
            }
            if (found < 0)
                throw new FrostyMcpToolException("No array entry matches '" + guid + "'.");
            if (!string.IsNullOrWhiteSpace(expectedType) && !TypeMatches(instance, expectedType))
                throw new FrostyMcpToolException("Object is " + instance.GetType().Name + ", not " + expectedType + ".");

            EnsureResizable(list);
            list.RemoveAt(found);
            asset.RemoveObject(instance);
            Finish(writebacks);
            FrostyMcpTools.CommitAsset(asset, entry);

            int removedAt = found;
            object removedInstance = instance;
            object removedStored = stored;
            EbxMcpUndo.Push(entry.Name, guid, "Remove object " + guid, delegate
            {
                EbxAsset live = FrostyMcpTools.LoadEditable(entry);
                bool present = false;
                foreach (object existing in live.Objects)
                {
                    if (ReferenceEquals(existing, removedInstance))
                    {
                        present = true;
                        break;
                    }
                }
                if (!present)
                    live.AddObject(removedInstance);
                PropertyInfo ignored;
                List<Writeback> liveWrites;
                IList liveList = OpenList(live.RootObject, path, out ignored, out liveWrites);
                int insertAt = removedAt > liveList.Count ? liveList.Count : removedAt;
                liveList.Insert(insertAt, removedStored);
                Finish(liveWrites);
                FrostyMcpTools.CommitAsset(live, entry);
            });

            return new JObject
            {
                ["asset"] = entry.Name,
                ["guid"] = guid,
                ["type"] = instance.GetType().Name,
                ["index"] = found,
                ["dirty"] = true
            };
        }

        private static JObject Validate(JObject args)
        {
            if (!Ready())
                return Idle();

            bool warnings = FrostyMcpTools.ArgBool(args, "includeWarnings", true);
            List<EbxAssetEntry> entries = new List<EbxAssetEntry>();
            string scope;
            if (args.Property("assets") == null)
            {
                scope = "modified";
                foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx("", true))
                    entries.Add(entry);
            }
            else
            {
                scope = "assets";
                JArray names = args["assets"] as JArray;
                if (names == null)
                    throw new FrostyMcpToolException("'assets' must be an array of asset names.");
                foreach (JToken name in names)
                {
                    if (name.Type != JTokenType.String || string.IsNullOrWhiteSpace(name.ToString()))
                        throw new FrostyMcpToolException("'assets' must contain asset names.");
                    entries.Add(FrostyMcpTools.RequireEntry(name.ToString()));
                }
            }

            List<Issue> errors = new List<Issue>();
            List<Issue> warningList = new List<Issue>();
            bool truncated = false;
            foreach (EbxAssetEntry entry in entries)
            {
                if (errors.Count >= 200)
                {
                    truncated = true;
                    break;
                }
                try
                {
                    ValidateAsset(entry, errors, warningList, warnings, ref truncated);
                }
                catch (Exception ex)
                {
                    AddIssue(errors, "error", entry.Name, "", "", ex.GetBaseException().Message, ref truncated);
                }
            }

            JArray errorArray = new JArray();
            foreach (Issue issue in errors)
                errorArray.Add(IssueObject(issue));
            JArray warningArray = new JArray();
            if (warnings)
            {
                foreach (Issue issue in warningList)
                    warningArray.Add(IssueObject(issue));
            }

            return new JObject
            {
                ["valid"] = errors.Count == 0,
                ["scope"] = scope,
                ["checked"] = entries.Count,
                ["errors"] = errorArray,
                ["warnings"] = warningArray,
                ["truncated"] = truncated
            };
        }

        private static void ValidateAsset(EbxAssetEntry entry, List<Issue> errors, List<Issue> warnings, bool includeWarnings, ref bool truncated)
        {
            EbxAsset asset = FrostyMcpTools.ReadAsset(entry);
            HashSet<object> members = new HashSet<object>(IdentityComparer.Instance);
            Dictionary<string, string> guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (object instance in asset.Objects)
            {
                members.Add(instance);
                AssetClassGuid guid;
                if (!TryGuid(instance, out guid))
                    continue;
                string key = guid.ToString();
                string previous;
                if (guids.TryGetValue(key, out previous))
                {
                    AddIssue(errors, "error", entry.Name, key, "", "Duplicate GUID " + key + " is also used by " + previous + ".", ref truncated);
                }
                else
                {
                    guids[key] = instance.GetType().Name;
                }
            }

            foreach (object instance in asset.Objects)
            {
                string guid = ReadGuid(instance) ?? "";
                ValidateValue(instance, entry, guid, instance.GetType().Name, members, errors, warnings, includeWarnings, 0, ref truncated);
            }
        }

        private static void ValidateValue(object value, EbxAssetEntry entry, string guid, string path, HashSet<object> members, List<Issue> errors, List<Issue> warnings, bool includeWarnings, int depth, ref bool truncated)
        {
            if (value == null || depth > 16 || errors.Count >= 200)
                return;

            if (value is PointerRef pointer)
            {
                if (pointer.Type == PointerRefType.Internal)
                {
                    if (pointer.Internal == null)
                        AddIssue(errors, "error", entry.Name, guid, path, "Internal reference is null.", ref truncated);
                    else if (!members.Contains(pointer.Internal))
                        AddIssue(errors, "error", entry.Name, guid, path, "Internal reference does not point at an object in this asset.", ref truncated);
                }
                else if (pointer.Type == PointerRefType.External)
                {
                    if (pointer.External.FileGuid == Guid.Empty)
                        AddIssue(errors, "error", entry.Name, guid, path, "External reference has no file GUID.", ref truncated);
                    else if (App.AssetManager.GetEbxEntry(pointer.External.FileGuid) == null)
                        AddIssue(errors, "error", entry.Name, guid, path, "External reference points at missing asset " + pointer.External.FileGuid + ".", ref truncated);
                    else if (includeWarnings && pointer.External.ClassGuid == Guid.Empty)
                        AddIssue(warnings, "warning", entry.Name, guid, path, "External reference has no class GUID.", ref truncated);
                }
                return;
            }

            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string))
            {
                if (type.IsEnum && type.GetCustomAttribute<FlagsAttribute>() == null && !Enum.IsDefined(type, value))
                    AddIssue(errors, "error", entry.Name, guid, path, "Enum value '" + value + "' is not defined on " + type.Name + ".", ref truncated);
                return;
            }

            IList list = value as IList;
            if (list != null)
            {
                Type element = ElementType(type);
                for (int i = 0; i < list.Count; i++)
                {
                    object item = list[i];
                    if (item != null && element != null && element != typeof(object) && !element.IsInstanceOfType(item))
                        AddIssue(errors, "error", entry.Name, guid, path + "[" + i + "]", "Array item is " + item.GetType().Name + ", not " + element.Name + ".", ref truncated);
                    ValidateValue(item, entry, guid, path + "[" + i + "]", members, errors, warnings, includeWarnings, depth + 1, ref truncated);
                }
                return;
            }

            if (type.Namespace == "FrostySdk.Ebx")
                return;

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod == null || property.Name.StartsWith("__", StringComparison.Ordinal))
                    continue;
                if (property.GetCustomAttribute<IsTransientAttribute>(true) != null)
                    continue;

                object field;
                try
                {
                    field = property.GetValue(value, null);
                }
                catch (Exception ex)
                {
                    AddIssue(errors, "error", entry.Name, guid, path + "." + property.Name, ex.GetBaseException().Message, ref truncated);
                    continue;
                }

                if (field != null && !property.PropertyType.IsInstanceOfType(field))
                    AddIssue(errors, "error", entry.Name, guid, path + "." + property.Name, "Value is " + field.GetType().Name + ", not " + property.PropertyType.Name + ".", ref truncated);
                else if (includeWarnings && field == null && IsRequired(property, property.GetCustomAttribute<EbxFieldMetaAttribute>(true)) && !property.PropertyType.IsValueType)
                    AddIssue(warnings, "warning", entry.Name, guid, path + "." + property.Name, "Required field is null.", ref truncated);

                ValidateValue(field, entry, guid, path + "." + property.Name, members, errors, warnings, includeWarnings, depth + 1, ref truncated);
            }
        }

        private static JObject Undo(JObject args)
        {
            if (!Ready())
                return Idle();
            string asset = FrostyMcpTools.ArgString(args, "asset");
            string guid = FrostyMcpTools.ArgString(args, "guid");
            return FrostyMcpTools.OnUi(delegate { return EbxMcpUndo.UndoLast(asset, guid); });
        }

        private static object RequireObject(EbxAsset asset, string guid)
        {
            object found = null;
            foreach (object instance in asset.Objects)
            {
                if (!EbxMcpSerializer.GuidEquals(instance, guid))
                    continue;
                if (found != null)
                    throw new FrostyMcpToolException("More than one object matches '" + guid + "'.");
                found = instance;
            }
            if (found == null)
                throw new FrostyMcpToolException("Object '" + guid + "' was not found.");
            return found;
        }

        private static Slot Resolve(object root, List<object> path)
        {
            if (path == null || path.Count == 0)
                throw new FrostyMcpToolException("The path is empty.");

            List<Writeback> writebacks = new List<Writeback>();
            object parent = path.Count == 1 ? root : Follow(root, path.GetRange(0, path.Count - 1), writebacks);
            object step = path[path.Count - 1];
            string name = step as string;
            if (name != null)
            {
                if (parent == null)
                    throw new FrostyMcpToolException("'" + FormatPath(path) + "' cannot be reached because its parent is null.");
                PropertyInfo property = FindProperty(parent.GetType(), name);
                if (property == null)
                    throw new FrostyMcpToolException("Field '" + name + "' was not found on '" + parent.GetType().Name + "'.");
                return new Slot { Owner = parent, Property = property, Writebacks = writebacks };
            }

            IList list = parent as IList;
            if (list == null)
                throw new FrostyMcpToolException("'" + FormatPath(path) + "' is not an array.");
            int index = (int)step;
            if (index < 0 || index >= list.Count)
                throw new FrostyMcpToolException("Index " + index + " is past the end of an array of " + list.Count + ".");
            return new Slot { List = list, Index = index, Writebacks = writebacks };
        }

        private static IList OpenList(object root, List<object> path, out PropertyInfo property, out List<Writeback> writebacks)
        {
            writebacks = new List<Writeback>();
            property = null;
            if (path == null || path.Count == 0)
                throw new FrostyMcpToolException("The array path is empty.");

            object current = root;
            for (int i = 0; i < path.Count; i++)
            {
                object step = path[i];
                string name = step as string;
                if (name != null)
                {
                    if (current == null)
                        throw new FrostyMcpToolException("'" + FormatPath(path) + "' cannot be reached because its parent is null.");
                    PropertyInfo next = FindProperty(current.GetType(), name);
                    if (next == null)
                        throw new FrostyMcpToolException("Field '" + name + "' was not found on '" + current.GetType().Name + "'.");
                    object value = next.GetValue(current, null);
                    if (i == path.Count - 1)
                        property = next;
                    else if (next.PropertyType.IsValueType && next.PropertyType != typeof(PointerRef))
                        writebacks.Add(new Writeback { Property = next, Owner = current, Box = value });
                    current = value;
                }
                else
                {
                    IList list = current as IList;
                    if (list == null)
                        throw new FrostyMcpToolException("'" + FormatPath(path) + "' is not an array.");
                    int index = (int)step;
                    if (index < 0 || index >= list.Count)
                        throw new FrostyMcpToolException("Index " + index + " is past the end of an array of " + list.Count + ".");
                    object item = list[index];
                    if (item != null && item.GetType().IsValueType && !(item is PointerRef))
                        writebacks.Add(new Writeback { List = list, Index = index, Box = item });
                    current = item;
                }
            }

            IList result = current as IList;
            if (result == null || current is string)
                throw new FrostyMcpToolException("'" + FormatPath(path) + "' is not an array.");
            return result;
        }

        private static object Follow(object root, List<object> path, List<Writeback> writebacks)
        {
            object current = root;
            for (int i = 0; i < path.Count; i++)
            {
                object step = path[i];
                string name = step as string;
                if (name != null)
                {
                    if (current == null)
                        throw new FrostyMcpToolException("'" + FormatPath(path) + "' cannot be reached because its parent is null.");
                    PropertyInfo property = FindProperty(current.GetType(), name);
                    if (property == null)
                        throw new FrostyMcpToolException("Field '" + name + "' was not found on '" + current.GetType().Name + "'.");
                    object value = property.GetValue(current, null);
                    if (property.PropertyType.IsValueType && property.PropertyType != typeof(PointerRef))
                        writebacks.Add(new Writeback { Property = property, Owner = current, Box = value });
                    current = value;
                }
                else
                {
                    IList list = current as IList;
                    if (list == null)
                        throw new FrostyMcpToolException("'" + FormatPath(path) + "' is not an array.");
                    int index = (int)step;
                    if (index < 0 || index >= list.Count)
                        throw new FrostyMcpToolException("Index " + index + " is past the end of an array of " + list.Count + ".");
                    object item = list[index];
                    if (item != null && item.GetType().IsValueType && !(item is PointerRef))
                        writebacks.Add(new Writeback { List = list, Index = index, Box = item });
                    current = item;
                }
            }
            return current;
        }

        private static void Finish(List<Writeback> writebacks)
        {
            for (int i = writebacks.Count - 1; i >= 0; i--)
                writebacks[i].Apply();
        }

        private static object ConvertValue(JToken token, Type targetType, EbxAsset asset, EbxAssetEntry entry, object current, PropertyInfo property)
        {
            if (targetType == null)
                targetType = typeof(object);

            if (token == null || token.Type == JTokenType.Null)
            {
                if (targetType == typeof(PointerRef))
                    return new PointerRef();
                if (targetType == typeof(CString))
                    return default(CString);
                if (targetType == typeof(string) || !targetType.IsValueType)
                    return null;
                throw new FrostyMcpToolException("This value cannot be null.");
            }

            if (targetType == typeof(byte[]))
                throw new FrostyMcpToolException("Byte arrays cannot be edited through MCP.");
            if (targetType == typeof(bool))
                return ConvertBool(token);
            if (targetType.IsEnum)
                return ConvertEnum(token, targetType);
            if (IsNumber(targetType))
                return ConvertNumber(token, targetType);
            if (targetType == typeof(string))
                return token.ToString();
            if (targetType == typeof(CString))
                return new CString(token.ToString());
            if (targetType == typeof(FileRef))
                return new FileRef(token.ToString());
            if (targetType == typeof(TypeRef))
                return new TypeRef(token.ToString());
            if (targetType == typeof(Guid))
                return ParseGuid(token.ToString(), "GUID");
            if (targetType == typeof(AssetClassGuid))
                return new AssetClassGuid(ParseGuid(token.ToString(), "GUID"), -1);
            if (targetType == typeof(Sha1))
            {
                try
                {
                    return new Sha1(token.ToString().Trim());
                }
                catch (Exception ex)
                {
                    throw new FrostyMcpToolException("Invalid Sha1: " + ex.GetBaseException().Message);
                }
            }
            if (targetType == typeof(ResourceRef))
                return ConvertResource(token);
            if (targetType == typeof(PointerRef))
                return ConvertPointer(token, asset, property);
            if (targetType == typeof(BoxedValueRef))
                return ConvertBoxed(token, asset, entry, current as BoxedValueRef);

            if (typeof(IList).IsAssignableFrom(targetType) && targetType != typeof(string))
                return ConvertList(token, targetType, asset, entry, current as IList, property);

            JObject body = token as JObject;
            if (body != null)
                return ConvertObject(body, targetType, asset, entry);

            throw new FrostyMcpToolException("Cannot convert " + token.Type + " to " + targetType.Name + ".");
        }

        private static object ConvertBool(JToken token)
        {
            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>();
            bool value;
            if (bool.TryParse(token.ToString(), out value))
                return value;
            throw new FrostyMcpToolException("Expected a boolean.");
        }

        private static object ConvertEnum(JToken token, Type type)
        {
            try
            {
                if (token.Type == JTokenType.String)
                    return Enum.Parse(type, token.Value<string>(), true);
                if (token.Type == JTokenType.Integer)
                    return Enum.ToObject(type, token.Value<long>());
            }
            catch (Exception ex)
            {
                throw new FrostyMcpToolException("Invalid " + type.Name + " value: " + ex.GetBaseException().Message);
            }
            throw new FrostyMcpToolException("Expected a " + type.Name + " name or number.");
        }

        private static object ConvertNumber(JToken token, Type target)
        {
            JObject hash = token as JObject;
            if (hash != null)
            {
                if (hash["value"] != null)
                    token = hash["value"];
                else if (hash["hash"] != null && (target == typeof(int) || target == typeof(uint)))
                {
                    int hashed = Utils.HashString(hash["hash"].ToString());
                    return target == typeof(uint) ? (object)unchecked((uint)hashed) : hashed;
                }
                else
                    throw new FrostyMcpToolException("Expected a number.");
            }

            if (token.Type == JTokenType.String)
            {
                string text = token.Value<string>().Trim();
                if (target == typeof(float))
                {
                    float value;
                    if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        return value;
                }
                if (target == typeof(double))
                {
                    double value;
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        return value;
                }
                if (target == typeof(ulong))
                {
                    ulong value;
                    if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                        return value;
                }
                if (target == typeof(long))
                {
                    long value;
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                        return value;
                }
                throw new FrostyMcpToolException("Expected a number.");
            }

            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new FrostyMcpToolException("Expected a number.");

            try
            {
                if (target == typeof(float))
                    return token.Value<float>();
                if (target == typeof(double))
                    return token.Value<double>();
                if (token.Type == JTokenType.Float)
                {
                    double real = token.Value<double>();
                    if (Math.Abs(real - Math.Round(real)) > 0.0000001d)
                        throw new FrostyMcpToolException("Expected a whole number.");
                }
                long raw = token.Type == JTokenType.Integer ? token.Value<long>() : (long)Math.Round(token.Value<double>());
                if (target == typeof(byte)) return checked((byte)raw);
                if (target == typeof(sbyte)) return checked((sbyte)raw);
                if (target == typeof(short)) return checked((short)raw);
                if (target == typeof(ushort)) return checked((ushort)raw);
                if (target == typeof(int)) return checked((int)raw);
                if (target == typeof(uint))
                {
                    if (raw < 0)
                        throw new OverflowException();
                    return checked((uint)raw);
                }
                if (target == typeof(long)) return raw;
                if (target == typeof(ulong))
                {
                    if (raw < 0)
                        throw new OverflowException();
                    return (ulong)raw;
                }
            }
            catch (OverflowException)
            {
                throw new FrostyMcpToolException("Number is outside the range of " + target.Name + ".");
            }

            throw new FrostyMcpToolException("Expected a " + target.Name + ".");
        }

        private static ResourceRef ConvertResource(JToken token)
        {
            string text = token.ToString().Trim();
            ulong id;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                id = ulong.Parse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            else if (text.Length == 16)
                id = ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            else
                id = ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            return new ResourceRef(id);
        }

        private static PointerRef ConvertPointer(JToken token, EbxAsset asset, PropertyInfo property)
        {
            if (token.Type == JTokenType.String)
                return InternalPointer(asset, token.ToString(), property);

            JObject obj = token as JObject;
            if (obj == null)
                throw new FrostyMcpToolException("A reference must be null, a guid, or an object with '$ref'.");

            string kind = obj["$ref"] != null ? obj["$ref"].ToString() : "";
            if (kind.Length == 0 || string.Equals(kind, "null", StringComparison.OrdinalIgnoreCase))
                return new PointerRef();
            if (string.Equals(kind, "internal", StringComparison.OrdinalIgnoreCase))
                return InternalPointer(asset, obj["guid"] != null ? obj["guid"].ToString() : null, property);
            if (!string.Equals(kind, "external", StringComparison.OrdinalIgnoreCase))
                throw new FrostyMcpToolException("Unknown reference kind '" + kind + "'.");

            Guid fileGuid;
            if (obj["fileGuid"] != null && Guid.TryParse(obj["fileGuid"].ToString(), out fileGuid))
            {
            }
            else if (obj["asset"] != null)
            {
                EbxAssetEntry external = App.AssetManager.GetEbxEntry(obj["asset"].ToString());
                if (external == null)
                    throw new FrostyMcpToolException("External asset '" + obj["asset"] + "' was not found.");
                fileGuid = external.Guid;
            }
            else
            {
                throw new FrostyMcpToolException("An external reference needs 'fileGuid' or 'asset'.");
            }

            string classText = obj["classGuid"] != null ? obj["classGuid"].ToString() : (obj["guid"] != null ? obj["guid"].ToString() : null);
            return new PointerRef(new EbxImportReference
            {
                FileGuid = fileGuid,
                ClassGuid = ParseGuid(classText, "classGuid")
            });
        }

        private static PointerRef InternalPointer(EbxAsset asset, string guid, PropertyInfo property)
        {
            if (asset == null || string.IsNullOrWhiteSpace(guid))
                throw new FrostyMcpToolException("An internal reference needs a guid.");
            object instance = RequireObject(asset, guid);
            if (property != null)
            {
                EbxFieldMetaAttribute meta = property.GetCustomAttribute<EbxFieldMetaAttribute>(true);
                if (meta != null && meta.BaseType != null && meta.BaseType != typeof(object) && meta.BaseType != typeof(PointerRef) && !meta.BaseType.IsAssignableFrom(instance.GetType()))
                    throw new FrostyMcpToolException("The reference points at " + instance.GetType().Name + ", which is not a " + meta.BaseType.Name + ".");
            }
            return new PointerRef(instance);
        }

        private static BoxedValueRef ConvertBoxed(JToken token, EbxAsset asset, EbxAssetEntry entry, BoxedValueRef current)
        {
            JObject body = token as JObject;
            string typeName = body != null && body["boxedType"] != null ? body["boxedType"].ToString() : null;
            JToken inner = body != null && (body.Property("value") != null || body.Property("boxedType") != null) ? body["value"] : token;
            if (string.IsNullOrEmpty(typeName))
            {
                if (current == null || current.Value == null)
                    throw new FrostyMcpToolException("A boxed value needs 'boxedType'.");
                object value = ConvertValue(inner, current.Value.GetType(), asset, entry, current.Value, null);
                return new BoxedValueRef(value, current.Type, current.ArrayType, current.Category);
            }

            EbxFieldType fieldType;
            if (Enum.TryParse(typeName, true, out fieldType) && fieldType != EbxFieldType.Struct && fieldType != EbxFieldType.Enum && fieldType != EbxFieldType.Array)
            {
                Type clr = BoxedClrType(fieldType);
                if (clr == null)
                    throw new FrostyMcpToolException("Boxed type '" + typeName + "' cannot be edited.");
                return new BoxedValueRef(ConvertValue(inner, clr, asset, entry, null, null), fieldType);
            }

            Type structType = TypeLibrary.GetType(typeName);
            if (structType == null)
                throw new FrostyMcpToolException("Boxed type '" + typeName + "' was not found.");
            object created = ConvertValue(inner ?? new JObject(), structType, asset, entry, null, null);
            return new BoxedValueRef(created, structType.IsEnum ? EbxFieldType.Enum : EbxFieldType.Struct);
        }

        private static object ConvertList(JToken token, Type targetType, EbxAsset asset, EbxAssetEntry entry, IList current, PropertyInfo property)
        {
            JArray items = token as JArray;
            if (items == null)
                throw new FrostyMcpToolException("Expected an array.");

            IList list = current;
            if (list == null || list.IsFixedSize || list.IsReadOnly)
            {
                if (targetType.IsInterface || targetType.IsAbstract)
                    throw new FrostyMcpToolException("The array type " + targetType.Name + " cannot be created.");
                list = (IList)Activator.CreateInstance(targetType);
            }
            else
            {
                list.Clear();
            }

            Type element = ElementType(list.GetType()) ?? typeof(object);
            foreach (JToken item in items)
                list.Add(ConvertValue(item, element, asset, entry, null, property));
            return list;
        }

        private static object ConvertObject(JObject body, Type targetType, EbxAsset asset, EbxAssetEntry entry)
        {
            object created = TypeLibrary.CreateObject(targetType.Name) ?? Activator.CreateInstance(targetType);
            foreach (JProperty property in body.Properties())
            {
                if (property.Name.StartsWith("__", StringComparison.Ordinal))
                    throw new FrostyMcpToolException("Field '" + property.Name + "' is assigned by the server.");
                PropertyInfo info = FindProperty(created.GetType(), property.Name);
                if (info == null)
                    throw new FrostyMcpToolException("Field '" + property.Name + "' was not found on '" + created.GetType().Name + "'.");
                if (info.GetCustomAttribute<IsReadOnlyAttribute>() != null || !info.CanWrite)
                    throw new FrostyMcpToolException("Field '" + info.Name + "' is read only.");
                info.SetValue(created, ConvertValue(property.Value, info.PropertyType, asset, entry, null, info), null);
            }
            return created;
        }

        private static void Expect(object current, JToken expected, Type declared, EbxAsset asset, EbxAssetEntry entry, PropertyInfo property, string label)
        {
            Type type = current != null ? current.GetType() : declared;
            object converted;
            try
            {
                converted = ConvertValue(expected, type ?? typeof(object), asset, entry, current, property);
            }
            catch (FrostyMcpToolException ex)
            {
                throw new FrostyMcpToolException(label + " does not match the expected value: " + ex.Message);
            }
            if (!Same(current, converted, 0))
                throw new FrostyMcpToolException(label + " changed since it was read. The current value is " + Preview(current) + ".");
        }

        private static bool Same(object left, object right, int depth)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;
            if (left is PointerRef leftPointer && right is PointerRef rightPointer)
                return SamePointer(leftPointer, rightPointer);
            if (left is float leftFloat && right is float rightFloat)
                return Math.Abs(leftFloat - rightFloat) <= 0.00001f;
            if (left is double leftDouble && right is double rightDouble)
                return Math.Abs(leftDouble - rightDouble) <= 0.0000001d;
            if (left is BoxedValueRef leftBoxed && right is BoxedValueRef rightBoxed)
                return leftBoxed.Type == rightBoxed.Type && Same(leftBoxed.Value, rightBoxed.Value, depth + 1);

            Type type = left.GetType();
            if (type != right.GetType())
                return false;
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid) || type == typeof(CString) || type == typeof(FileRef) || type == typeof(ResourceRef) || type == typeof(Sha1) || type == typeof(AssetClassGuid))
                return left.Equals(right);
            if (depth > 6)
                return left.Equals(right);

            IList leftList = left as IList;
            IList rightList = right as IList;
            if (leftList != null && rightList != null)
            {
                if (leftList.Count != rightList.Count)
                    return false;
                for (int i = 0; i < leftList.Count; i++)
                {
                    if (!Same(leftList[i], rightList[i], depth + 1))
                        return false;
                }
                return true;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod == null || property.Name.StartsWith("__", StringComparison.Ordinal))
                    continue;
                object leftValue;
                object rightValue;
                try
                {
                    leftValue = property.GetValue(left, null);
                    rightValue = property.GetValue(right, null);
                }
                catch
                {
                    return false;
                }
                if (!Same(leftValue, rightValue, depth + 1))
                    return false;
            }
            return true;
        }

        private static bool SamePointer(PointerRef left, PointerRef right)
        {
            if (left.Type != right.Type)
                return false;
            if (left.Type == PointerRefType.Internal)
                return ReferenceEquals(left.Internal, right.Internal);
            if (left.Type == PointerRefType.External)
                return left.External.FileGuid == right.External.FileGuid && left.External.ClassGuid == right.External.ClassGuid;
            return true;
        }

        private static void ApplyFields(object instance, JObject fields, EbxAsset asset, EbxAssetEntry entry)
        {
            foreach (JProperty property in fields.Properties())
            {
                if (property.Name.StartsWith("__", StringComparison.Ordinal) || string.Equals(property.Name, "guid", StringComparison.OrdinalIgnoreCase))
                    throw new FrostyMcpToolException("The server assigns the object GUID.");
                PropertyInfo info = FindProperty(instance.GetType(), property.Name);
                if (info == null)
                    throw new FrostyMcpToolException("Field '" + property.Name + "' was not found on '" + instance.GetType().Name + "'.");
                if (!info.CanWrite || info.GetCustomAttribute<IsReadOnlyAttribute>() != null)
                    throw new FrostyMcpToolException("Field '" + info.Name + "' is read only.");
                if (info.GetCustomAttribute<IsTransientAttribute>(true) != null)
                    throw new FrostyMcpToolException("Field '" + info.Name + "' is transient and is not saved.");
                info.SetValue(instance, ConvertValue(property.Value, info.PropertyType, asset, entry, null, info), null);
            }
        }

        private static string AssignGuid(object instance, EbxAsset asset, EbxAssetEntry entry)
        {
            if (!IsPointerClass(instance.GetType()))
                return null;
            try
            {
                Guid exported = Utils.GenerateDeterministicGuid(asset.Objects, instance.GetType(), asset.FileGuid);
                AssetClassGuid guid = new AssetClassGuid(exported, -1);
                ((dynamic)instance).SetInstanceGuid(guid);
                return guid.ToString();
            }
            catch (Exception ex)
            {
                throw new FrostyMcpToolException("Could not assign a GUID: " + ex.GetBaseException().Message);
            }
        }

        private static void ApplyPeerFlags(object instance, JObject fields, string guidText)
        {
            if (fields.Property("Flags") != null || string.IsNullOrEmpty(guidText) || !TypeLibrary.IsSubClassOf(instance, "DataBusPeer"))
                return;
            PropertyInfo flags = instance.GetType().GetProperty("Flags", BindingFlags.Public | BindingFlags.Instance);
            if (flags == null || !flags.CanWrite)
                return;
            Guid guid;
            if (!Guid.TryParse(guidText, out guid))
                return;
            byte[] bytes = guid.ToByteArray();
            uint mask = (uint)((bytes[2] << 16) | (bytes[1] << 8) | bytes[0]);
            if (flags.PropertyType == typeof(uint))
                flags.SetValue(instance, mask, null);
            else if (flags.PropertyType == typeof(int))
                flags.SetValue(instance, unchecked((int)mask), null);
        }

        private static object BoxForList(object instance, Type elementType)
        {
            if (elementType == typeof(PointerRef))
                return new PointerRef(instance);
            if (elementType == typeof(object) && IsPointerClass(instance.GetType()))
                return new PointerRef(instance);
            if (elementType == typeof(object) || elementType.IsInstanceOfType(instance))
                return instance;
            throw new FrostyMcpToolException("The array stores " + elementType.Name + ", not " + instance.GetType().Name + ".");
        }

        private static void RemoveStored(IList list, object instance)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                object item = list[i];
                if (ReferenceEquals(item, instance) || (item is PointerRef && ReferenceEquals(((PointerRef)item).Internal, instance)))
                {
                    list.RemoveAt(i);
                    return;
                }
            }
        }

        private static object Unwrap(object item)
        {
            if (item is PointerRef pointer)
                return pointer.Type == PointerRefType.Internal ? pointer.Internal : null;
            return item;
        }

        private static bool IsPointerClass(Type type)
        {
            EbxClassMetaAttribute meta = type.GetCustomAttribute<EbxClassMetaAttribute>();
            return meta != null && meta.Type == EbxFieldType.Pointer;
        }

        private static bool IsRequired(PropertyInfo property, EbxFieldMetaAttribute meta)
        {
            Type type = property.PropertyType;
            if (type == typeof(PointerRef) || type == typeof(BoxedValueRef) || type == typeof(string) || type == typeof(CString) || type == typeof(FileRef) || type == typeof(TypeRef) || type == typeof(ResourceRef))
                return false;
            if (typeof(IList).IsAssignableFrom(type) && type != typeof(string))
                return false;
            if (meta != null && (meta.IsArray || meta.Type == EbxFieldType.Pointer || meta.Type == EbxFieldType.Array || meta.Type == EbxFieldType.CString || meta.Type == EbxFieldType.String || meta.Type == EbxFieldType.FileRef || meta.Type == EbxFieldType.ResourceRef || meta.Type == EbxFieldType.TypeRef || meta.Type == EbxFieldType.BoxedValueRef))
                return false;
            return true;
        }

        private static string ValueKind(Type type, EbxFieldMetaAttribute meta)
        {
            if (meta != null && meta.IsArray)
                return "array";
            if (typeof(IList).IsAssignableFrom(type) && type != typeof(string))
                return "array";
            if (type == typeof(PointerRef) || (meta != null && meta.Type == EbxFieldType.Pointer))
                return "reference";
            if (type.IsEnum || (meta != null && meta.Type == EbxFieldType.Enum))
                return "enum";
            if (IsNumber(type) || type == typeof(bool))
                return "primitive";
            if (type == typeof(string) || type == typeof(CString) || type == typeof(FileRef) || type == typeof(TypeRef))
                return "string";
            return "struct";
        }

        private static bool IsNumber(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
                || type == typeof(float) || type == typeof(double);
        }

        private static Type BoxedClrType(EbxFieldType type)
        {
            switch (type)
            {
                case EbxFieldType.Boolean: return typeof(bool);
                case EbxFieldType.Int8: return typeof(sbyte);
                case EbxFieldType.UInt8: return typeof(byte);
                case EbxFieldType.Int16: return typeof(short);
                case EbxFieldType.UInt16: return typeof(ushort);
                case EbxFieldType.Int32: return typeof(int);
                case EbxFieldType.UInt32: return typeof(uint);
                case EbxFieldType.Int64: return typeof(long);
                case EbxFieldType.UInt64: return typeof(ulong);
                case EbxFieldType.Float32: return typeof(float);
                case EbxFieldType.Float64: return typeof(double);
                case EbxFieldType.CString: return typeof(CString);
                case EbxFieldType.String: return typeof(string);
                case EbxFieldType.Guid: return typeof(Guid);
                case EbxFieldType.Sha1: return typeof(Sha1);
                case EbxFieldType.ResourceRef: return typeof(ResourceRef);
                case EbxFieldType.FileRef: return typeof(FileRef);
                case EbxFieldType.TypeRef: return typeof(TypeRef);
                default: return null;
            }
        }

        private static Type ElementType(Type type)
        {
            if (type == null)
                return null;
            if (type.IsArray)
                return type.GetElementType();
            if (type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();
                if (arguments.Length == 1)
                    return arguments[0];
            }
            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            PropertyInfo exact = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (exact != null && exact.GetIndexParameters().Length == 0)
                return exact;

            PropertyInfo match = null;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || !string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (match != null)
                    throw new FrostyMcpToolException("Field '" + name + "' is ambiguous on '" + type.Name + "'.");
                match = property;
            }
            return match;
        }

        private static bool TypeMatches(object instance, string expected)
        {
            if (instance == null || string.IsNullOrWhiteSpace(expected))
                return false;
            if (string.Equals(instance.GetType().Name, expected, StringComparison.OrdinalIgnoreCase))
                return true;
            return TypeLibrary.IsSubClassOf(instance.GetType(), expected);
        }

        private static bool TryGuid(object instance, out AssetClassGuid guid)
        {
            guid = default(AssetClassGuid);
            if (instance == null)
                return false;
            try
            {
                guid = ((dynamic)instance).GetInstanceGuid();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadGuid(object instance)
        {
            AssetClassGuid guid;
            return TryGuid(instance, out guid) ? guid.ToString() : null;
        }

        private static Guid ParseGuid(string text, string label)
        {
            Guid guid;
            if (string.IsNullOrWhiteSpace(text) || !Guid.TryParse(text.Trim(), out guid))
                throw new FrostyMcpToolException("'" + label + "' is not a GUID.");
            return guid;
        }

        private static List<object> ReadPath(JToken token, string label)
        {
            JArray array = token as JArray;
            if (array == null || array.Count == 0)
                throw new FrostyMcpToolException("'" + label + "' must be a non-empty array of field names and indexes.");

            List<object> steps = new List<object>(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                JToken step = array[i];
                if (step.Type == JTokenType.String)
                {
                    string name = step.Value<string>();
                    if (string.IsNullOrWhiteSpace(name))
                        throw new FrostyMcpToolException("'" + label + "' segment " + i + " is empty.");
                    steps.Add(name);
                }
                else if (step.Type == JTokenType.Integer)
                {
                    long index = step.Value<long>();
                    if (index < 0 || index > int.MaxValue)
                        throw new FrostyMcpToolException("'" + label + "' index " + index + " is out of range.");
                    steps.Add((int)index);
                }
                else
                {
                    throw new FrostyMcpToolException("'" + label + "' segment " + i + " must be a string or an integer.");
                }
            }
            return steps;
        }

        private static int RequiredIndex(JObject args, string name)
        {
            JToken token = args?[name];
            if (token == null || token.Type != JTokenType.Integer)
                throw new FrostyMcpToolException("'" + name + "' must be an integer.");
            long value = token.Value<long>();
            if (value < 0 || value > int.MaxValue)
                throw new FrostyMcpToolException("'" + name + "' must be zero or greater.");
            return (int)value;
        }

        private static void EnsureResizable(IList list)
        {
            if (list.IsReadOnly || list.IsFixedSize)
                throw new FrostyMcpToolException("The array cannot be resized.");
        }

        private static string FormatPath(List<object> path)
        {
            StringBuilder builder = new StringBuilder();
            foreach (object step in path)
            {
                string name = step as string;
                if (name != null)
                {
                    if (builder.Length > 0)
                        builder.Append('.');
                    builder.Append(name);
                }
                else
                {
                    builder.Append('[').Append((int)step).Append(']');
                }
            }
            return builder.ToString();
        }

        private static JArray PathArray(List<object> path)
        {
            JArray array = new JArray();
            foreach (object step in path)
            {
                string name = step as string;
                array.Add(name != null ? (JToken)new JValue(name) : new JValue((int)step));
            }
            return array;
        }

        private static JArray Names(Type enumType)
        {
            JArray names = new JArray();
            foreach (string name in Enum.GetNames(enumType))
                names.Add(name);
            return names;
        }

        private static JToken PreviewToken(object value)
        {
            try
            {
                return EbxMcpSerializer.WriteValue(value);
            }
            catch
            {
                return new JValue(value == null ? "null" : value.ToString());
            }
        }

        private static string Preview(object value)
        {
            JToken token = PreviewToken(value);
            string text = token.ToString(Newtonsoft.Json.Formatting.None);
            return text.Length > 180 ? text.Substring(0, 180) + "..." : text;
        }

        private static void AddIssue(List<Issue> issues, string severity, string asset, string guid, string path, string message, ref bool truncated)
        {
            if (issues.Count >= 200)
            {
                truncated = true;
                return;
            }
            issues.Add(new Issue { Severity = severity, Asset = asset, Guid = guid, Path = path, Message = message });
        }

        private static JObject IssueObject(Issue issue)
        {
            return new JObject
            {
                ["severity"] = issue.Severity,
                ["asset"] = issue.Asset ?? "",
                ["guid"] = issue.Guid ?? "",
                ["path"] = issue.Path ?? "",
                ["message"] = issue.Message ?? ""
            };
        }

        private static bool Ready()
        {
            return ProfilesLibrary.HasLoadedProfile && App.AssetManager != null;
        }

        private static JObject Idle()
        {
            return new JObject { ["loaded"] = false, ["message"] = "Game profile is not loaded." };
        }

        private static JObject PathSchema(string description)
        {
            JObject schema = new JObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JObject { ["type"] = new JArray("string", "integer") }
            };
            if (!string.IsNullOrEmpty(description))
                schema["description"] = description;
            return schema;
        }

        private static JObject IndexSchema(string description)
        {
            JObject schema = new JObject { ["type"] = "integer", ["minimum"] = 0 };
            if (!string.IsNullOrEmpty(description))
                schema["description"] = description;
            return schema;
        }

        private static JObject BoolDefault(string description, bool value)
        {
            return new JObject { ["type"] = "boolean", ["description"] = description, ["default"] = value };
        }
    }
}
