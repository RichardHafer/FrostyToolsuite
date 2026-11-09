using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using FrostySdk;
using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;

namespace FrostyEditor.Mcp
{
    /// <summary>
    /// Deep-copies one EBX class into another asset. External pointers stay external.
    /// Sibling layer entities are not cloned: in the same asset they keep their internal
    /// pointer, and across assets they become an external reference back to the source file.
    /// </summary>
    internal static class EbxObjectCloner
    {
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        public static object ClonePreservingSharedReferences(object source, EbxAsset sourceAsset, EbxAssetEntry sourceEntry, EbxAsset targetAsset, EbxAssetEntry targetEntry)
        {
            if (source == null)
                throw new FrostyMcpToolException("The source object was not found.");

            foreach (object root in sourceAsset.RootObjects)
            {
                if (ReferenceEquals(root, source))
                    throw new FrostyMcpToolException("The asset root cannot be cloned.");
            }

            HashSet<object> siblings = CollectShared(sourceAsset, source);
            Dictionary<object, object> mapping = new Dictionary<object, object>(ReferenceComparer.Instance);
            return CopyValue(source, sourceAsset, sourceEntry, targetAsset, targetEntry, siblings, mapping);
        }

        public static object CloneInto(object source, EbxAsset sourceAsset, EbxAssetEntry sourceEntry, EbxAsset targetAsset, EbxAssetEntry targetEntry)
        {
            HashSet<object> siblings = new HashSet<object>(ReferenceComparer.Instance);
            foreach (object entity in LayerEntities(sourceAsset))
            {
                if (!ReferenceEquals(entity, source))
                    siblings.Add(entity);
            }

            Dictionary<object, object> mapping = new Dictionary<object, object>(ReferenceComparer.Instance);
            return CopyValue(source, sourceAsset, sourceEntry, targetAsset, targetEntry, siblings, mapping);
        }

        public static IList LayerObjects(EbxAsset asset)
        {
            object root = asset.RootObject;
            if (root == null)
                throw new FrostyMcpToolException("The layer has no root object.");

            PropertyInfo property = root.GetType().GetProperty("Objects", BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
                throw new FrostyMcpToolException("The layer has no Objects array.");

            IList list = property.GetValue(root, null) as IList;
            if (list == null)
                throw new FrostyMcpToolException("The layer Objects array cannot be edited.");
            return list;
        }

        public static object FindLayerEntity(EbxAsset asset, string guid, out int index)
        {
            IList objects = LayerObjects(asset);
            int found = -1;
            object entity = null;
            for (int i = 0; i < objects.Count; i++)
            {
                PointerRef pointer;
                if (!TryGetPointer(objects[i], out pointer) || pointer.Type != PointerRefType.Internal || pointer.Internal == null)
                    continue;
                if (!EbxMcpSerializer.GuidEquals(pointer.Internal, guid))
                    continue;
                if (found != -1)
                    throw new FrostyMcpToolException("More than one Objects entry matches '" + guid + "'.");
                found = i;
                entity = pointer.Internal;
            }

            index = found;
            return entity;
        }

        private static HashSet<object> CollectShared(EbxAsset asset, object source)
        {
            HashSet<object> shared = new HashSet<object>(ReferenceComparer.Instance);
            Queue<object> pending = new Queue<object>();
            foreach (object root in asset.RootObjects)
            {
                if (root == null || ReferenceEquals(root, source))
                    continue;
                if (shared.Add(root))
                    pending.Enqueue(root);
            }

            while (pending.Count > 0)
            {
                object current = pending.Dequeue();
                foreach (object target in InternalPointers(current))
                {
                    if (target == null || ReferenceEquals(target, source))
                        continue;
                    if (shared.Add(target))
                        pending.Enqueue(target);
                }
            }

            return shared;
        }

        private static List<object> InternalPointers(object instance)
        {
            List<object> found = new List<object>();
            HashSet<object> seen = new HashSet<object>(ReferenceComparer.Instance);
            CollectPointers(instance, seen, found);
            return found;
        }

        private static void CollectPointers(object instance, HashSet<object> seen, List<object> found)
        {
            if (instance == null)
                return;

            Type type = instance.GetType();
            if (type == typeof(PointerRef))
            {
                PointerRef pointer = (PointerRef)instance;
                if (pointer.Type == PointerRefType.Internal && pointer.Internal != null)
                    found.Add(pointer.Internal);
                return;
            }

            if (type.IsPrimitive || type.IsEnum || type == typeof(string))
                return;

            IList list = instance as IList;
            if (list != null)
            {
                foreach (object item in list)
                    CollectPointers(item, seen, found);
                return;
            }

            if (!type.IsValueType && !seen.Add(instance))
                return;

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod == null || property.Name.StartsWith("__", StringComparison.Ordinal))
                    continue;

                object value;
                try
                {
                    value = property.GetValue(instance, null);
                }
                catch
                {
                    continue;
                }

                CollectPointers(value, seen, found);
            }
        }

        private static List<object> LayerEntities(EbxAsset asset)
        {
            List<object> entities = new List<object>();
            foreach (object item in LayerObjects(asset))
            {
                PointerRef pointer;
                if (TryGetPointer(item, out pointer) && pointer.Type == PointerRefType.Internal && pointer.Internal != null)
                    entities.Add(pointer.Internal);
            }

            return entities;
        }

        private static bool TryGetPointer(object value, out PointerRef pointer)
        {
            if (value is PointerRef boxed)
            {
                pointer = boxed;
                return true;
            }

            pointer = default(PointerRef);
            return false;
        }

        private static object CopyValue(object value, EbxAsset sourceAsset, EbxAssetEntry sourceEntry, EbxAsset targetAsset, EbxAssetEntry targetEntry, HashSet<object> siblings, Dictionary<object, object> mapping)
        {
            if (value == null)
                return null;

            Type type = value.GetType();
            if (type == typeof(PointerRef))
                return CopyPointer((PointerRef)value, sourceAsset, sourceEntry, targetAsset, targetEntry, siblings, mapping);

            if (type.IsArray)
            {
                Array sourceArray = (Array)value;
                return sourceArray.Clone();
            }

            if (typeof(IList).IsAssignableFrom(type))
            {
                IList sourceList = (IList)value;
                IList copy = (IList)Activator.CreateInstance(type);
                copy.Clear();
                foreach (object item in sourceList)
                    copy.Add(CopyValue(item, sourceAsset, sourceEntry, targetAsset, targetEntry, siblings, mapping));
                return copy;
            }

            return CopyInstance(value, sourceAsset, sourceEntry, targetAsset, targetEntry, siblings, mapping);
        }

        private static PointerRef CopyPointer(PointerRef pointer, EbxAsset sourceAsset, EbxAssetEntry sourceEntry, EbxAsset targetAsset, EbxAssetEntry targetEntry, HashSet<object> siblings, Dictionary<object, object> mapping)
        {
            if (pointer.Type == PointerRefType.External)
                return new PointerRef(pointer.External);
            if (pointer.Type != PointerRefType.Internal || pointer.Internal == null)
                return new PointerRef();

            object existing;
            if (mapping.TryGetValue(pointer.Internal, out existing))
                return new PointerRef(existing);

            if (siblings.Contains(pointer.Internal))
            {
                if (ReferenceEquals(sourceAsset, targetAsset))
                    return new PointerRef(pointer.Internal);

                AssetClassGuid siblingGuid = ((dynamic)pointer.Internal).GetInstanceGuid();
                if (siblingGuid.IsExported)
                {
                    return new PointerRef(new EbxImportReference
                    {
                        FileGuid = sourceEntry.Guid,
                        ClassGuid = siblingGuid.ExportedGuid
                    });
                }
            }

            object copy = CopyInstance(pointer.Internal, sourceAsset, sourceEntry, targetAsset, targetEntry, siblings, mapping);
            return new PointerRef(copy);
        }

        private static object CopyInstance(object source, EbxAsset sourceAsset, EbxAssetEntry sourceEntry, EbxAsset targetAsset, EbxAssetEntry targetEntry, HashSet<object> siblings, Dictionary<object, object> mapping)
        {
            Type type = source.GetType();
            if (type.IsPrimitive || type.IsValueType || type.GetCustomAttribute<EbxClassMetaAttribute>() == null)
                return source;

            object existing;
            if (mapping.TryGetValue(source, out existing))
                return existing;

            object copy = TypeLibrary.CreateObject(type.Name) ?? Activator.CreateInstance(type);
            mapping.Add(source, copy);

            EbxClassMetaAttribute meta = type.GetCustomAttribute<EbxClassMetaAttribute>();
            if (meta != null && meta.Type == EbxFieldType.Pointer)
            {
                AssetClassGuid guid = ((dynamic)source).GetInstanceGuid();
                if (guid.IsExported)
                    guid = new AssetClassGuid(Utils.GenerateDeterministicGuid(targetAsset.Objects, copy.GetType(), targetEntry.Guid), -1);
                else
                    guid = new AssetClassGuid(-1);
                ((dynamic)copy).SetInstanceGuid(guid);
                targetAsset.AddObject(copy);
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || !property.CanWrite || property.Name.StartsWith("__", StringComparison.Ordinal))
                    continue;

                object propertyValue = property.GetValue(source, null);
                if (propertyValue == null)
                {
                    if (!property.PropertyType.IsValueType)
                        property.SetValue(copy, null, null);
                    continue;
                }

                property.SetValue(copy, CopyValue(propertyValue, sourceAsset, sourceEntry, targetAsset, targetEntry, siblings, mapping), null);
            }

            return copy;
        }
    }
}
