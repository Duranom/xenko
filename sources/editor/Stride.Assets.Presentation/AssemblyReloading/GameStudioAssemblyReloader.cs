// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Assets.Serializers;
using Stride.Core.Assets.Visitors;
using Stride.Core.Assets.Yaml;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Diagnostics;
using Stride.Core.Extensions;
using Stride.Core.Reflection;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Events;
using Stride.Core.Yaml.Serialization;
using Stride.Core.Presentation.Dirtiables;
using Stride.Core.Presentation.Services;
using Stride.Core.Quantum;

namespace Stride.Assets.Presentation.AssemblyReloading
{
    /// <summary>
    /// Helper to reload game assemblies within the editor.
    /// </summary>
    public static class GameStudioAssemblyReloader
    {
        public static void Reload([NotNull] SessionViewModel session, ILogger log, Action postReloadAction, Action undoAction, [NotNull] Dictionary<PackageLoadedAssembly, string> modifiedAssemblies)
        {
            var loadedAssemblies = modifiedAssemblies.Where(x => File.Exists(x.Key.Path)).ToDictionary(x => x.Key, x => x.Value);

            var assemblyContainer = session.AssemblyContainer;

            using (session.CreateAssetFixupContext())
            {
                // TODO: Filter by "modified assemblies", for now we reload everything
                var loadedAssembliesSet = new HashSet<Assembly>(loadedAssemblies.Select(x => x.Key.Assembly).NotNull());

                // Serialize types from unloaded assemblies as Yaml, and unset them
                var unloadingVisitor = new UnloadingVisitor(log, loadedAssembliesSet);
                Dictionary<AssetViewModel, List<ItemToReload>> assetItemsToReload;
                try
                {
                    assetItemsToReload = PrepareAssemblyReloading(session, unloadingVisitor, session.UndoRedoService);
                }
                catch (Exception e)
                {
                    log.Error( "Could not prepare asset for assembly reload", e);
                    throw;
                }

                var reloadOperation = new ReloadAssembliesOperation(assemblyContainer, modifiedAssemblies, []);
                session.UndoRedoService.SetName(reloadOperation, "Reload assemblies");

                // Reload assemblies
                reloadOperation.Execute(log);
                session.UndoRedoService.PushOperation(reloadOperation);

                postReloadAction();
                var postReloadOperation = new AnonymousDirtyingOperation([], postReloadAction, postReloadAction);
                session.UndoRedoService.PushOperation(postReloadOperation);
                session.UndoRedoService.SetName(postReloadOperation, "Post reload action");

                // Restore deserialized objects (or IUnloadable if it didn't work)
                try
                {
                    PostAssemblyReloading(session.UndoRedoService, session.AssetNodeContainer, log, assetItemsToReload);
                }
                catch (Exception e)
                {
                    log.Error("Could not restore asset after assembly reload", e);
                    throw;
                }

                var undoOperation = new AnonymousDirtyingOperation(Enumerable.Empty<IDirtiable>(), undoAction, null);
                session.UndoRedoService.PushOperation(undoOperation);
                session.UndoRedoService.SetName(undoOperation, "Undo action");

            }

            session.ActiveProperties.RefreshSelectedPropertiesAsync().Forget();
        }

        private static Dictionary<AssetViewModel, List<ItemToReload>> PrepareAssemblyReloading(SessionViewModel session, UnloadingVisitor visitor, IUndoRedoService actionService)
        {
            var assetItemsToReload = new Dictionary<AssetViewModel, List<ItemToReload>>();

            // Serialize old objects from unloaded assemblies to Yaml streams
            foreach (var asset in session.LocalPackages.SelectMany(x => x.Assets))
            {
                // Asset has no quantum graph (eg. text asset), let's skip it
                if (asset.PropertyGraph == null)
                    continue;

                // Generate missing ids for safetly
                AssetCollectionItemIdHelper.GenerateMissingItemIds(asset.Asset);

                // Serialize objects with types to unload to Yaml
                // Objects that were already IUnloadable will also be included
                var rootNode = session.AssetNodeContainer.GetNode(asset.Asset);
                var itemsToReload = visitor.Run(asset.PropertyGraph);

                if (itemsToReload.Count > 0)
                {
                    // We apply changes in opposite visit order so that indices remains valid when we remove objects while iterating
                    for (int index = itemsToReload.Count - 1; index >= 0; index--)
                    {
                        var itemToReload = itemsToReload[index];

                        // Transform path to Quantum
                        itemToReload.GraphPath = GraphNodePath.From(rootNode, itemToReload.Path, out itemToReload.GraphPathIndex);

                        // Remove (collections) or replace nodes with null (members) so that we can unload assemblies
                        ClearNode(actionService, asset, itemToReload);
                    }

                    // Add to list of items to reload for PostAssemblyReloading
                    assetItemsToReload.Add(asset, new List<ItemToReload>(itemsToReload));
                }
            }

            return assetItemsToReload;
        }

        private static void PostAssemblyReloading(IUndoRedoService actionService, SessionNodeContainer nodeContainer, ILogger log, Dictionary<AssetViewModel, List<ItemToReload>> assetItemsToReload)
        {
            log?.Info("Updating components with newly loaded assemblies");

            // Recreate new objects from Yaml streams
            foreach (var asset in assetItemsToReload)
            {
                // Deserialize objects with reloaded types from Yaml
                var objectReferences = new YamlAssetMetadata<Guid>();
                foreach (var itemToReload in asset.Value)
                {
                    var expectedPath = itemToReload.Path.Decompose().LastOrDefault()?.GetIndex() != null ? itemToReload.ParentPath : itemToReload.Path;
                    if (expectedPath.TryGetValue(asset.Key.Asset, out _) == false)
                        continue;

                    var eventReader = new EventReader(new MemoryParser(itemToReload.ParsingEvents));
                    var settings = log != null ? new SerializerContextSettings { Logger = log } : null;
                    PropertyContainer properties;
                    itemToReload.UpdatedObject = AssetYamlSerializer.Default.Deserialize(eventReader, null, itemToReload.ExpectedType, out properties, settings);

                    // We will have broken references here because we are deserializing objects individually, so we don't pass any logger to discard warnings
                    var metadata = YamlAssetSerializer.CreateAndProcessMetadata(properties, itemToReload.UpdatedObject, false);

                    itemToReload.Overrides = metadata.RetrieveMetadata(AssetObjectSerializerBackend.OverrideDictionaryKey);

                    var references = metadata.RetrieveMetadata(AssetObjectSerializerBackend.ObjectReferencesKey);
                    if (references != null)
                    {
                        var basePath = YamlAssetPath.FromMemberPath(expectedPath, asset.Key.Asset);
                        foreach (var reference in references)
                        {
                            var basePathWithIndex = basePath.Clone();
                            if (itemToReload.GraphPathIndex != NodeIndex.Empty)
                            {
                                if (itemToReload.ItemId == ItemId.Empty)
                                    basePathWithIndex.PushIndex(itemToReload.GraphPathIndex.Value);
                                else
                                    basePathWithIndex.PushItemId(itemToReload.ItemId);
                            }

                            var actualPath = basePathWithIndex.Append(reference.Key);
                            objectReferences.Set(actualPath, reference.Value);
                        }
                    }
                }

                // Set new values
                var overrides = new YamlAssetMetadata<OverrideType>();
                foreach (var itemToReload in asset.Value)
                {
                    // Set (members) or add nodes (collections) with values created using newly loaded assemblies
                    ReplaceNode(actionService, asset.Key, itemToReload);
                    if (itemToReload.Overrides != null)
                    {
                        var extendedPath = itemToReload.GraphPath.Clone();
                        if (itemToReload.GraphPathIndex != NodeIndex.Empty)
                            extendedPath.PushIndex(itemToReload.GraphPathIndex);

                        var pathToPrepend = AssetNodeMetadataCollectorBase.ConvertPath(extendedPath);

                        foreach (var entry in itemToReload.Overrides)
                        {
                            var path = pathToPrepend.Append(entry.Key);
                            overrides.Set(path, entry.Value);
                        }
                    }
                }

                FixupObjectReferences.RunFixupPass(asset.Key.Asset, objectReferences, true, false, log);

                var rootNode = (IAssetNode)nodeContainer.GetNode(asset.Key.Asset);            
                AssetPropertyGraph.ApplyOverrides(rootNode, overrides);
            }
        }

        private static void ClearNode(IUndoRedoService actionService, AssetViewModel asset, ItemToReload itemToReload)
        {
            // Get the node we want to change
            var index = itemToReload.GraphPathIndex;
            var node = itemToReload.GraphPath.GetNode();
            var oldValue = node.Retrieve(index);

            // Apply the change
            // TODO: Share this code with ContentValueChangeOperation?
            // TODO: How to better detect CollectionAdd vs ValueChange?
            ContentChangeType operationType;
            if (index != NodeIndex.Empty)
            {
                CollectionItemIdentifiers ids;
                if (CollectionItemIdHelper.TryGetCollectionItemIds(node.Retrieve(), out ids))
                {
                    itemToReload.ItemId = ids[index.Value];
                }
                operationType = ContentChangeType.CollectionRemove;
                ((IObjectNode)node).Remove(oldValue, index);
            }
            else if (node is IMemberNode memberNode)
            {
                operationType = ContentChangeType.ValueChange;
                memberNode.Update(null);
            }
            else if (node is IObjectNode objectNode)
            {
                var unloadedAsset = (Asset)UnloadableObjectInstantiator.CreateUnloadableObject(typeof(Asset), itemToReload.ExpectedType.Name, "Test", "Reloading", itemToReload.ParsingEvents);
                unloadedAsset.Id = asset.Id;
                asset.UpdateAsset(unloadedAsset, new LoggerResult());
                operationType = ContentChangeType.ValueChange;
                //objectNode.Update(unloadedAsset, NodeIndex.Empty);
            }
            else
            {
                throw new NotImplementedException();
            }

            // Save the change on the stack
            var operation = new ContentValueChangeOperation(node, operationType, index, oldValue, null, Enumerable.Empty<IDirtiable>());
            actionService.PushOperation(operation);
            string operationName = $"Unload object {oldValue.GetType().Name} in asset {asset.Url}";
            actionService.SetName(operation, operationName);
        }

        private static void ReplaceNode(IUndoRedoService actionService, AssetViewModel asset, ItemToReload itemToReload)
        {
            // Get the node we want to change
            var graphPath = itemToReload.GraphPath;
            var index = itemToReload.GraphPathIndex;
            var node = graphPath.GetNode();

            // Apply the change
            // TODO: Share this code with ContentValueChangeOperation?
            // TODO: How to better detect CollectionAdd vs ValueChange?
            ContentChangeType operationType;
            if (index != NodeIndex.Empty)
            {
                operationType = ContentChangeType.CollectionAdd;
                ((IAssetObjectNode)node).Restore(itemToReload.UpdatedObject, index, itemToReload.ItemId);
            }
            else if (node is IMemberNode memberNode)
            {
                operationType = ContentChangeType.ValueChange;
                memberNode.Update(itemToReload.UpdatedObject);
            }
            else if (node is IObjectNode objectNode)
            {
                operationType = ContentChangeType.ValueChange;
                //objectNode.Update(itemToReload.UpdatedObject, NodeIndex.Empty);
                asset.UpdateAsset((Asset)itemToReload.UpdatedObject, new LoggerResult());
            }
            else
            {
                throw new NotImplementedException();
            }

            // Save the change on the stack
            var operation = new ContentValueChangeOperation(node, operationType, index, null, itemToReload.UpdatedObject, asset.Dirtiables);
            actionService.PushOperation(operation);
            string operationName = $"Reload object {itemToReload.UpdatedObject.GetType().Name} in asset {asset.Url}";
            actionService.SetName(operation, operationName);
        }

        /// <summary>
        /// Serializes and deserializes part of assets that needs reloading.
        /// </summary>
        private abstract class ReloaderVisitorBase : AssetVisitorBase
        {
            protected readonly HashSet<Assembly> UnloadedAssemblies;
            protected readonly ILogger Log;
            protected List<ItemToReload> ItemsToReload;

            protected ReloaderVisitorBase(ILogger log, HashSet<Assembly> unloadedAssemblies)
            {
                Log = log;
                UnloadedAssemblies = unloadedAssemblies;
            }

            public override void VisitObject(object obj, ObjectDescriptor descriptor, bool visitMembers)
            {
                // For asset roots (not reference), we want the base type rather than the real one (which will be unloaded)
                var expectedType = descriptor.Type;
                if (obj is Asset)
                    expectedType = typeof(Asset);

                if (ProcessObject(obj, expectedType)) return;

                base.VisitObject(obj, descriptor, visitMembers);
            }

            public override void VisitCollectionItem(IEnumerable collection, CollectionDescriptor descriptor, int index, object item, ITypeDescriptor itemDescriptor)
            {
                if (ProcessObject(item, descriptor.ElementType)) return;

                base.VisitCollectionItem(collection, descriptor, index, item, itemDescriptor);
            }

            public override void VisitArrayItem(Array array, ArrayDescriptor descriptor, int index, object item, ITypeDescriptor itemDescriptor)
            {
                if (ProcessObject(item, descriptor.ElementType)) return;

                base.VisitArrayItem(array, descriptor, index, item, itemDescriptor);
            }

            public override void VisitDictionaryKeyValue(object dictionary, DictionaryDescriptor descriptor, object key, ITypeDescriptor keyDescriptor, object value, ITypeDescriptor valueDescriptor)
            {
                // TODO: CurrentPath is valid only for value, not key
                //if (ProcessObject(key, keyDescriptor.Type)) key = null;
                if (ProcessObject(value, valueDescriptor.Type)) return;

                Visit(value, valueDescriptor);
                //base.VisitDictionaryKeyValue(dictionary, descriptor, key, keyDescriptor, value, valueDescriptor);
            }

            public override void VisitSetItem(IEnumerable set, SetDescriptor descriptor, object item, ITypeDescriptor itemDescriptor)
            {
                if (ProcessObject(item, itemDescriptor.Type)) return;

                base.VisitSetItem(set, descriptor, item, itemDescriptor);
            }

            protected abstract bool ProcessObject(object obj, Type expectedType);
        }

        private class UnloadingVisitor : ReloaderVisitorBase
        {
            private AssetPropertyGraph propertyGraph;

            public UnloadingVisitor(ILogger log, HashSet<Assembly> unloadedAssemblies)
                : base(log, unloadedAssemblies)
            {
            }

            [NotNull]
            public List<ItemToReload> Run([NotNull] AssetPropertyGraph assetPropertyGraph)
            {
                if (assetPropertyGraph == null) throw new ArgumentNullException(nameof(assetPropertyGraph));
                Reset();
                propertyGraph = assetPropertyGraph;
                var result = ItemsToReload = new List<ItemToReload>();
                Visit(assetPropertyGraph.RootNode.Retrieve());
                ItemsToReload = null;
                return result;
            }

            protected override bool ProcessObject(object obj, Type expectedType)
            {
                // TODO: More advanced checks if IUnloadable is supposed to be a type from the unloaded assembly (this would avoid processing unecessary IUnloadable)
                if (obj != null && (UnloadedAssemblies.Contains(obj.GetType().Assembly) || obj is IUnloadable))
                {
                    NodeIndex index;
                    var settings = new SerializerContextSettings(Log);
                    var path = GraphNodePath.From(propertyGraph.RootNode, CurrentPath, out index);

                    var overrides = AssetPropertyGraph.GenerateOverridesForSerialization(path.GetNode());
                    overrides = RemoveFirstIndexInYamlPath(overrides, index);
                    if (overrides != null)
                        settings.Properties.Add(AssetObjectSerializerBackend.OverrideDictionaryKey, overrides);

                    var objectReferences = propertyGraph.GenerateObjectReferencesForSerialization(path.GetNode());
                    objectReferences = RemoveFirstIndexInYamlPath(objectReferences, index);
                    if (objectReferences != null)
                        settings.Properties.Add(AssetObjectSerializerBackend.ObjectReferencesKey, objectReferences);

                    var parsingEvents = new List<ParsingEvent>();
                    AssetYamlSerializer.Default.Serialize(new ParsingEventListEmitter(parsingEvents), obj, expectedType, settings);

                    // Buid parent path
                    var parentPath = CurrentPath.Clone();
                    parentPath.Pop();

                    ItemsToReload.Add(new ItemToReload(parentPath, CurrentPath.Clone(), parsingEvents, expectedType));

                    // Don't recurse inside
                    return true;
                }
                return false;
            }

            private static YamlAssetMetadata<T> RemoveFirstIndexInYamlPath<T>([CanBeNull] YamlAssetMetadata<T> metadata, NodeIndex index)
            {
                if (metadata != null)
                {
                    // If we had an index we need to remove it from our override paths
                    if (index != NodeIndex.Empty)
                    {
                        var fixedMetadata = new YamlAssetMetadata<T>();
                        foreach (var entry in metadata)
                        {
                            // We're filling a new dictionary because we are destroying hash codes by muting the keys
                            var newPath = entry.Key.Elements.Count > 0 ? new YamlAssetPath(entry.Key.Elements.Skip(1)) : entry.Key;
                            fixedMetadata.Set(newPath, entry.Value);
                        }
                        metadata = fixedMetadata;
                    }
                }
                return metadata;
            }
        }

        /// <summary>
        /// Part of an asset to reload.
        /// </summary>
        private class ItemToReload
        {
            /// <summary>
            /// Path of the node containing the item.
            /// </summary>
            public readonly MemberPath ParentPath;

            /// <summary>
            /// Path of the item.
            /// </summary>
            public readonly MemberPath Path;

            /// <summary>
            /// The Yaml events.
            /// </summary>
            public readonly List<ParsingEvent> ParsingEvents;

            /// <summary>
            /// The expected type of the item (i.e. parent collection type or member type).
            /// </summary>
            public readonly Type ExpectedType;

            /// <summary>
            /// The converted graph path.
            /// </summary>
            public GraphNodePath GraphPath;

            /// <summary>
            /// The additional index to apply on the graph path to reach the item.
            /// </summary>
            public NodeIndex GraphPathIndex;

            /// <summary>
            /// The identifier of the item to reload, if relevant.
            /// </summary>
            public ItemId ItemId;

            /// <summary>
            /// The newly deserialized object with updated assemblies.
            /// </summary>
            public object UpdatedObject;

            public ItemToReload(MemberPath parentPath, MemberPath path, List<ParsingEvent> parsingEvents, Type expectedType)
            {
                ParentPath = parentPath;
                Path = path;
                ParsingEvents = parsingEvents;
                ExpectedType = expectedType;
            }

            public YamlAssetMetadata<OverrideType> Overrides { get; set; }

            public override string ToString() => $"[{Path}] {ExpectedType}";
        }
    }
}
