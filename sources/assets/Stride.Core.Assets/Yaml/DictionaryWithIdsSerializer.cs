// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections;
using System.Reflection;
using Stride.Core.Annotations;
using Stride.Core.Reflection;
using Stride.Core.Yaml.Serialization;

namespace Stride.Core.Yaml;

/// <summary>
/// An implementation of <see cref="CollectionWithIdsSerializerBase"/> for dictionaries.
/// </summary>
[YamlSerializerFactory("Assets")]
public class DictionaryWithIdsSerializer : CollectionWithIdsSerializerBase
{
    /// <inheritdoc/>
    public override IYamlSerializable? TryCreate(SerializerContext context, ITypeDescriptor typeDescriptor)
    {
        if (typeDescriptor is DictionaryDescriptor)
        {
            var identifiable = typeDescriptor.Type.GetCustomAttribute<NonIdentifiableCollectionItemsAttribute>();
            if (identifiable != null)
                return null;

            var dataStyle = typeDescriptor.Type.GetCustomAttribute<DataStyleAttribute>();
            if (dataStyle == null || dataStyle.Style != DataStyle.Compact)
                return this;
        }
        return null;
    }

    /// <inheritdoc/>
    protected override void TransformObjectAfterRead(ref ObjectContext objectContext)
    {
        if (!AreCollectionItemsIdentifiable(ref objectContext))
        {
            base.TransformObjectAfterRead(ref objectContext);
            return;
        }

        var info = (InstanceInfo)objectContext.Properties[InstanceInfoKey];

        // This is to be backward compatible with previous serialization. We fetch ids from the ~Id member of each item
        if (info.Instance != null)
        {
            objectContext.Properties.TryGetValue(DeletedItemsKey, out var deletedItems);
            TransformAfterDeserialization((IDictionary)objectContext.Instance, info.Descriptor, info.Instance, deletedItems);
        }
        objectContext.Instance = info.Instance;

        base.TransformObjectAfterRead(ref objectContext);
    }

    /// <inheritdoc/>
    protected override object TransformForSerialization(ITypeDescriptor descriptor, object collection)
    {
        var dictionaryDescriptor = (DictionaryDescriptor)descriptor;
        var instance = CreatEmptyContainer(descriptor);

        if (!CollectionItemIdHelper.TryGetCollectionItemIds(collection, out var identifier))
        {
            identifier = [];
        }
        var keyWithIdType = typeof(KeyWithId<>).MakeGenericType(dictionaryDescriptor.KeyType);
        foreach (var item in dictionaryDescriptor.GetEnumerator(collection))
        {
            if (!identifier.TryGet(item.Key, out var id))
            {
                id = ItemId.New();
                identifier.Add(item.Key, id);
            }
            var keyWithId = Activator.CreateInstance(keyWithIdType, id, item.Key)!;
            instance.Add(keyWithId, item.Value);
        }

        return instance;
    }

    /// <inheritdoc/>
    protected override IDictionary CreatEmptyContainer(ITypeDescriptor descriptor)
    {
        var dictionaryDescriptor = (DictionaryDescriptor)descriptor;
        var type = typeof(DictionaryWithItemIds<,>).MakeGenericType(dictionaryDescriptor.KeyType, dictionaryDescriptor.ValueType);
        if (type.GetConstructor(Type.EmptyTypes) == null)
            throw new InvalidOperationException("The type of dictionary does not have a parameterless constructor.");
        return (IDictionary)Activator.CreateInstance(type)!;
    }

    /// <inheritdoc/>
    protected override void TransformAfterDeserialization(IDictionary container, ITypeDescriptor targetDescriptor, object targetCollection, ICollection<ItemId>? deletedItems = null)
    {
        var dictionaryDescriptor = (DictionaryDescriptor)targetDescriptor;
        var type = typeof(DictionaryWithItemIds<,>).MakeGenericType(dictionaryDescriptor.KeyType, dictionaryDescriptor.ValueType);
        if (!type.IsInstanceOfType(container))
            throw new InvalidOperationException("The given container does not match the expected type.");
        var identifier = CollectionItemIdHelper.GetCollectionItemIds(targetCollection);
        identifier.Clear();

        // The collection may contain some initial data from its containing instance's ctor,
        // let's replace the existing data with the data we have serialized
        dictionaryDescriptor.Clear(targetCollection);

        var enumerator = container.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var keyWithId = (IKeyWithId)enumerator.Key;
            dictionaryDescriptor.AddToDictionary(targetCollection, keyWithId.Key, enumerator.Value);
            identifier.Add(keyWithId.Key, keyWithId.Id);
        }
        if (deletedItems != null)
        {
            foreach (var deletedItem in deletedItems)
            {
                identifier.MarkAsDeleted(deletedItem);
            }
        }
    }

    protected override void WriteDeletedItems(ref ObjectContext objectContext)
    {
        objectContext.Properties.TryGetValue(DeletedItemsKey, out var deletedItems);
        if (deletedItems != null)
        {
            var dictionaryDescriptor = (DictionaryDescriptor)objectContext.Descriptor;
            var keyWithIdType = typeof(DeletedKeyWithId<>).MakeGenericType(dictionaryDescriptor.KeyType);
            var keyValueType = new KeyValuePair<Type, Type>(keyWithIdType, typeof(string));
            foreach (var deletedItem in deletedItems)
            {
                // Add a ~ to allow to parse it back as a KeyWithId.
                var keyWithId = Activator.CreateInstance(keyWithIdType, deletedItem)!;
                var entry = new KeyValuePair<object, object>(keyWithId, YamlDeletedKey);
                WriteDictionaryItem(ref objectContext, entry, keyValueType);
            }
        }
    }

    protected override KeyValuePair<object, object> ReadDeletedDictionaryItem(ref ObjectContext objectContext, object keyResult)
    {
        var valueResult = objectContext.ObjectSerializerBackend.ReadDictionaryValue(ref objectContext, typeof(string), keyResult);
        var id = ((IKeyWithId)keyResult).Id;
        return new KeyValuePair<object, object>(id, valueResult);
    }
}
