﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SenseNet.Configuration;
using SenseNet.ContentRepository.Search.Indexing;
using SenseNet.ContentRepository.Storage.Caching.Dependency;
using SenseNet.ContentRepository.Storage.Data.SqlClient;
using SenseNet.ContentRepository.Storage.DataModel;
using SenseNet.ContentRepository.Storage.Schema;
using SenseNet.ContentRepository.Storage.Security;
using SenseNet.Diagnostics;
using SenseNet.Search.Indexing;
using SenseNet.Search.Querying;

// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

// ReSharper disable once CheckNamespace
namespace SenseNet.ContentRepository.Storage.Data
{
    public static class DataStore
    {
        // ReSharper disable once InconsistentNaming
        //UNDONE:DB -------Remove DataStore.__enabled
        private static bool __enabled;
        public static bool Enabled
        {
            get => __enabled;
            set
            {
                __enabled = value;
                BlobStorageComponents.DataStoreEnabled = value;
            }
        }

        public static DataProvider2 DataProvider => Providers.Instance.DataProvider2;

        public static int PathMaxLength => DataProvider.PathMaxLength;
        public static DateTime DateTimeMinValue => DataProvider.DateTimeMinValue;
        public static DateTime DateTimeMaxValue => DataProvider.DateTimeMaxValue;
        public static decimal DecimalMinValue => DataProvider.DecimalMinValue;
        public static decimal DecimalMaxValue => DataProvider.DecimalMaxValue;


        public static T GetDataProviderExtension<T>() where T : class, IDataProviderExtension
        {
            return DataProvider.GetExtensionInstance<T>();
        }

        public static void Reset()
        {
            DataProvider.Reset();
        }

        /* =============================================================================================== Installation */

        public static async Task InstallInitialDataAsync(InitialData data)
        {
            await DataProvider.InstallInitialDataAsync(data);
        }

        public static Task<IEnumerable<EntityTreeNodeData>> LoadEntityTreeAsync()
        {
            return DataProvider.LoadEntityTreeAsync();
        }

        /* =============================================================================================== Nodes */

        public static async Task SaveNodeAsync(NodeData nodeData, NodeSaveSettings settings, CancellationToken? cancellationToken = null)
        {
            // ORIGINAL SIGNATURES:
            // internal void SaveNodeData(NodeData nodeData, NodeSaveSettings settings, out int lastMajorVersionId, out int lastMinorVersionId)
            // private static void SaveNodeBaseData(NodeData nodeData, SavingAlgorithm savingAlgorithm, INodeWriter writer, NodeSaveSettings settings, out int lastMajorVersionId, out int lastMinorVersionId)
            // private static void SaveNodeProperties(NodeData nodeData, SavingAlgorithm savingAlgorithm, INodeWriter writer, bool isNewNode)
            // protected internal abstract INodeWriter CreateNodeWriter();
            // protected internal abstract void DeleteVersion(int versionId, NodeData nodeData, out int lastMajorVersionId, out int lastMinorVersionId);
            // -------------------
            // Before return the LastMajorVersionIdAfter and LastMinorVersionIdAfter properties of the given "settings" need to be updated
            //    instead of use the original output values.

            //UNDONE:DB ?Implement transaction related stuff (from DataBackingStore)
            //UNDONE:DB Implement cache invalidations (from DataBackingStore)

            cancellationToken?.ThrowIfCancellationRequested();

            if (nodeData == null)
                throw new ArgumentNullException(nameof(nodeData));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var isNewNode = nodeData.Id == 0;

            // SAVE DATA (head, version, dynamic metadata, binaries)
            // Do not block any exception from the called methods.
            // If need a catch block rethrow away the exception.

            var nodeHeadData = nodeData.GetNodeHeadData();
            var savingAlgorithm = settings.GetSavingAlgorithm();
            var renamed = !isNewNode && nodeData.PathChanged && nodeData.SharedData != null;
            if (settings.NeedToSaveData)
            {
                var versionData = nodeData.GetVersionData();
                DynamicPropertyData dynamicData;
                switch (savingAlgorithm)
                {
                    case SavingAlgorithm.CreateNewNode:
                        dynamicData = nodeData.GetDynamicData(false);
                        await DataProvider.InsertNodeAsync(nodeHeadData, versionData, dynamicData);
                        // Write back the new NodeId
                        nodeData.Id = nodeHeadData.NodeId;
                        break;
                    case SavingAlgorithm.UpdateSameVersion:
                        dynamicData = nodeData.GetDynamicData(false);
                        if(renamed)
                            await DataProvider.UpdateNodeAsync(
                                nodeHeadData, versionData, dynamicData, settings.DeletableVersionIds, nodeData.SharedData.Path);
                        else
                            await DataProvider.UpdateNodeAsync(
                                nodeHeadData, versionData, dynamicData, settings.DeletableVersionIds);
                        break;
                    case SavingAlgorithm.CopyToNewVersionAndUpdate:
                        dynamicData = nodeData.GetDynamicData(true);
                        if (renamed)
                            await DataProvider.CopyAndUpdateNodeAsync(
                                nodeHeadData, versionData, dynamicData, settings.DeletableVersionIds,
                                settings.CurrentVersionId, 0,
                                nodeData.SharedData.Path);
                        else
                            await DataProvider.CopyAndUpdateNodeAsync(
                                nodeHeadData, versionData, dynamicData, settings.DeletableVersionIds,
                                settings.CurrentVersionId);
                        break;
                    case SavingAlgorithm.CopyToSpecifiedVersionAndUpdate:
                        dynamicData = nodeData.GetDynamicData(true);
                        if (renamed)
                            await DataProvider.CopyAndUpdateNodeAsync(
                                nodeHeadData, versionData, dynamicData, settings.DeletableVersionIds,
                                settings.CurrentVersionId, settings.ExpectedVersionId,
                                nodeData.SharedData.Path);
                        else
                            await DataProvider.CopyAndUpdateNodeAsync(
                                nodeHeadData, versionData, dynamicData, settings.DeletableVersionIds,
                                settings.CurrentVersionId, settings.ExpectedVersionId);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("Unknown SavingAlgorithm: " + savingAlgorithm);
                }
                // Write back the version level changed values
                nodeData.VersionId = versionData.VersionId;
                nodeData.VersionTimestamp = versionData.Timestamp;

                //if (!isNewNode && nodeData.PathChanged && nodeData.SharedData != null)
                //    await DataProvider.UpdateSubTreePathAsync(nodeData.SharedData.Path, nodeData.Path);
            }
            else
            {
                await DataProvider.UpdateNodeHeadAsync(nodeHeadData, settings.DeletableVersionIds);
            }
            // Write back NodeHead level changed values
            settings.LastMajorVersionIdAfter = nodeHeadData.LastMajorVersionId;
            settings.LastMinorVersionIdAfter = nodeHeadData.LastMinorVersionId;
            nodeData.NodeTimestamp = nodeHeadData.Timestamp;
        }
        public static async Task<NodeToken[]> LoadNodesAsync(NodeHead[] headArray, int[] versionIdArray)
        {
            // ORIGINAL SIGNATURES:
            // internal void LoadNodeData(IEnumerable<NodeToken> tokens)
            // protected internal abstract void LoadNodes(Dictionary<int, NodeBuilder> buildersByVersionId);

            var tokens = new List<NodeToken>();
            var tokensToLoad = new List<NodeToken>();
            for (var i = 0; i < headArray.Length; i++)
            {
                var head = headArray[i];
                var versionId = versionIdArray[i];

                var token = new NodeToken(head.Id, head.NodeTypeId, head.ContentListId, head.ContentListTypeId, versionId, null)
                {
                    NodeHead = head
                };
                tokens.Add(token);

                var cacheKey = GenerateNodeDataVersionIdCacheKey(versionId);
                if (DistributedApplication.Cache.Get(cacheKey) is NodeData nodeData)
                    token.NodeData = nodeData;
                else
                    tokensToLoad.Add(token);
            }
            if (tokensToLoad.Count > 0)
            {
                var versionIds = tokensToLoad.Select(x => x.VersionId).ToArray();
                var loadedCollection = await DataProvider.LoadNodesAsync(versionIds);
                foreach (var nodeData in loadedCollection)
                {
                    if (nodeData != null) // lost version
                    {
                        CacheNodeData(nodeData);
                        var token = tokensToLoad.First(x => x.VersionId == nodeData.VersionId);
                        token.NodeData = nodeData;
                    }
                }
            }
            return tokens.ToArray();
        }
        public static async Task DeleteNodeAsync(NodeData nodeData)
        {
            // ORIGINAL SIGNATURES:
            // internal void DeleteNode(int nodeId)
            // internal void DeleteNodePsychical(int nodeId, long timestamp)
            // protected internal abstract DataOperationResult DeleteNodeTree(int nodeId);
            // protected internal abstract DataOperationResult DeleteNodeTreePsychical(int nodeId, long timestamp);
            // -------------------
            // The word as suffix "Tree" is unnecessary, "Psychical" is misleading.

            await DataProvider.DeleteNodeAsync(nodeData.GetNodeHeadData());
        }
        public static async Task MoveNodeAsync(NodeData sourceNodeData, int targetNodeId, long targetTimestamp)
        {
            // ORIGINAL SIGNATURES:
            // internal void MoveNode(int sourceNodeId, int targetNodeId, long sourceTimestamp, long targetTimestamp)
            // protected internal abstract DataOperationResult MoveNodeTree(int sourceNodeId, int targetNodeId, long sourceTimestamp = 0, long targetTimestamp = 0);
            var sourceNodeHeadData = sourceNodeData.GetNodeHeadData();
            await DataProvider.MoveNodeAsync(sourceNodeHeadData, targetNodeId, targetTimestamp);
        }

        public static Task<Dictionary<int, string>> LoadTextPropertyValuesAsync(int versionId, int[] notLoadedPropertyTypeIds)
        {
            return DataProvider.LoadTextPropertyValuesAsync(versionId, notLoadedPropertyTypeIds);
        }
        public static Task<BinaryDataValue> LoadBinaryPropertyValueAsync(int versionId, int propertyTypeId)
        {
            return DataProvider.LoadBinaryPropertyValueAsync(versionId, propertyTypeId);
        }

        public static Task<bool> NodeExistsAsync(string path)
        {
            return DataProvider.NodeExistsAsync(path);
        }

        /* =============================================================================================== NodeHead */

        public static Task<NodeHead> LoadNodeHeadAsync(string path)
        {
            return DataProvider.LoadNodeHeadAsync(path);
        }
        public static Task<NodeHead> LoadNodeHeadAsync(int nodeId)
        {
            return DataProvider.LoadNodeHeadAsync(nodeId);
        }
        public static Task<NodeHead> LoadNodeHeadByVersionIdAsync(int versionId)
        {
            return DataProvider.LoadNodeHeadByVersionIdAsync(versionId);
        }
        public static Task<IEnumerable<NodeHead>> LoadNodeHeadsAsync(IEnumerable<int> heads)
        {
            return DataProvider.LoadNodeHeadsAsync(heads);
        }
        public static Task<NodeHead.NodeVersion[]> GetNodeVersionsAsync(int nodeId)
        {
            return DataProvider.GetNodeVersions(nodeId);
        }
        public static Task<IEnumerable<VersionNumber>> GetVersionNumbersAsync(int nodeId)
        {
            return DataProvider.GetVersionNumbersAsync(nodeId);
        }
        public static Task<IEnumerable<VersionNumber>> GetVersionNumbersAsync(string path)
        {
            return DataProvider.GetVersionNumbersAsync(path);
        }

        /* =============================================================================================== NodeQuery */

        public static Task<int> InstanceCountAsync(int[] nodeTypeIds)
        {
            return DataProvider.InstanceCountAsync(nodeTypeIds);
        }
        public static Task<IEnumerable<int>> GetChildrenIdentfiersAsync(int parentId)
        {
            return DataProvider.GetChildrenIdentfiersAsync(parentId);
        }
        public static Task<IEnumerable<int>> QueryNodesByPathAsync(string pathStart, bool orderByPath)
        {
            return QueryNodesByTypeAndPathAsync(null, pathStart, orderByPath);
        }
        public static Task<IEnumerable<int>> QueryNodesByTypeAsync(int[] nodeTypeIds)
        {
            return QueryNodesByTypeAndPathAsync(nodeTypeIds, new string[0], false);
        }
        public static Task<IEnumerable<int>> QueryNodesByTypeAndPathAsync(int[] nodeTypeIds, string pathStart, bool orderByPath)
        {
            return QueryNodesByTypeAndPathAndNameAsync(nodeTypeIds, pathStart, orderByPath, null);
        }
        public static Task<IEnumerable<int>> QueryNodesByTypeAndPathAsync(int[] nodeTypeIds, string[] pathStart, bool orderByPath)
        {
            return QueryNodesByTypeAndPathAndNameAsync(nodeTypeIds, pathStart, orderByPath, null);
        }
        public static Task<IEnumerable<int>> QueryNodesByTypeAndPathAndNameAsync(int[] nodeTypeIds, string pathStart, bool orderByPath, string name)
        {
            return QueryNodesByTypeAndPathAndNameAsync(nodeTypeIds, new[] { pathStart }, orderByPath, name);
        }
        public static Task<IEnumerable<int>> QueryNodesByTypeAndPathAndNameAsync(int[] nodeTypeIds, string[] pathStart, bool orderByPath, string name)
        {
            return DataProvider.QueryNodesByTypeAndPathAndNameAsync(nodeTypeIds, pathStart, orderByPath, name);
        }
        public static Task<IEnumerable<int>> QueryNodesByTypeAndPathAndPropertyAsync(int[] nodeTypeIds, string pathStart, bool orderByPath, List<QueryPropertyData> properties)
        {
            return DataProvider.QueryNodesByTypeAndPathAndPropertyAsync(nodeTypeIds, pathStart, orderByPath, properties);
        }
        public static Task<IEnumerable<int>> QueryNodesByReferenceAndTypeAsync(string referenceName, int referredNodeId, int[] nodeTypeIds)
        {
            return DataProvider.QueryNodesByReferenceAndTypeAsync(referenceName, referredNodeId, nodeTypeIds);
        }

        /* =============================================================================================== Tree */

        public static Task<IEnumerable<NodeType>> LoadChildTypesToAllowAsync(int nodeId)
        {
            return DataProvider.LoadChildTypesToAllowAsync(nodeId);
        }
        public static Task<List<ContentListType>> GetContentListTypesInTreeAsync(string path)
        {
            return DataProvider.GetContentListTypesInTreeAsync(path);
        }

        /* =============================================================================================== TreeLock */

        public static Task<int> AcquireTreeLockAsync(string path)
        {
            return DataProvider.AcquireTreeLockAsync(path);
        }
        public static Task<bool> IsTreeLockedAsync(string path)
        {
            return DataProvider.IsTreeLockedAsync(path);
        }
        public static Task ReleaseTreeLockAsync(int[] lockIds)
        {
            return DataProvider.ReleaseTreeLockAsync(lockIds);
        }
        public static Task<Dictionary<int, string>> LoadAllTreeLocksAsync()
        {
            return DataProvider.LoadAllTreeLocksAsync();
        }

        /* =============================================================================================== IndexDocument */

        public static async Task SaveIndexDocumentAsync(NodeData nodeData, IndexDocument indexDoc)
        {
            await DataProvider.SaveIndexDocumentAsync(nodeData, indexDoc);
        }
        public static async Task SaveIndexDocumentAsync(int versionId, IndexDocument indexDoc)
        {
            await DataProvider.SaveIndexDocumentAsync(versionId, indexDoc);
        }

        public static async Task<IndexDocumentData> LoadIndexDocumentByVersionIdAsync(int versionId)
        {
            var result = await DataProvider.LoadIndexDocumentsAsync(new []{versionId});
            return result.FirstOrDefault();
        }
        public static Task<IEnumerable<IndexDocumentData>> LoadIndexDocumentsAsync(IEnumerable<int> versionIds)
        {
            return DataProvider.LoadIndexDocumentsAsync(versionIds);
        }
        public static Task<IEnumerable<IndexDocumentData>> LoadIndexDocumentsAsync(string path, int[] excludedNodeTypes)
        {
            return DataProvider.LoadIndexDocumentsAsync(path, excludedNodeTypes);
        }

        public static Task<IEnumerable<int>> LoadNotIndexedNodeIdsAsync(int fromId, int toId)
        {
            return DataProvider.LoadNotIndexedNodeIdsAsync(fromId, toId);
        }

        /* =============================================================================================== IndexingActivity */

        public static Task<int> GetLastIndexingActivityIdAsync()
        {
            return DataProvider.GetLastIndexingActivityIdAsync();
        }
        public static Task<IIndexingActivity[]> LoadIndexingActivitiesAsync(int fromId, int toId, int count, bool executingUnprocessedActivities, IIndexingActivityFactory activityFactory)
        {
            return DataProvider.LoadIndexingActivitiesAsync(fromId, toId, count, executingUnprocessedActivities, activityFactory);
        }
        public static Task<IIndexingActivity[]> LoadIndexingActivitiesAsync(int[] gaps, bool executingUnprocessedActivities, IIndexingActivityFactory activityFactory)
        {
            return DataProvider.LoadIndexingActivitiesAsync(gaps, executingUnprocessedActivities, activityFactory);
        }
        public static Task<IIndexingActivity[]> LoadExecutableIndexingActivitiesAsync(IIndexingActivityFactory activityFactory, int maxCount, int runningTimeoutInSeconds)
        {
            return DataProvider.LoadExecutableIndexingActivitiesAsync(activityFactory, maxCount, runningTimeoutInSeconds);
        }
        public static Task<ExecutableIndexingActivitiesResult> LoadExecutableIndexingActivitiesAsync(IIndexingActivityFactory activityFactory, int maxCount, int runningTimeoutInSeconds, int[] waitingActivityIds)
        {
            return DataProvider.LoadExecutableIndexingActivitiesAsync(activityFactory, maxCount, runningTimeoutInSeconds, waitingActivityIds);
        }
        public static async Task RegisterIndexingActivityAsync(IIndexingActivity activity)
        {
            await DataProvider.RegisterIndexingActivityAsync(activity);
        }
        public static async Task UpdateIndexingActivityRunningStateAsync(int indexingActivityId, IndexingActivityRunningState runningState)
        {
            await DataProvider.UpdateIndexingActivityRunningStateAsync(indexingActivityId, runningState);
        }
        public static async Task RefreshIndexingActivityLockTimeAsync(int[] waitingIds)
        {
            await DataProvider.RefreshIndexingActivityLockTimeAsync(waitingIds);
        }
        public static async Task DeleteFinishedIndexingActivitiesAsync()
        {
            await DataProvider.DeleteFinishedIndexingActivitiesAsync();
        }
        public static async Task DeleteAllIndexingActivitiesAsync()
        {
            await DataProvider.DeleteAllIndexingActivitiesAsync();
        }

        /* =============================================================================================== Schema */

        public static Task<RepositorySchemaData> LoadSchemaAsync()
        {
            return DataProvider.LoadSchemaAsync();
        }

        public static string StartSchemaUpdate_EXPERIMENTAL(long schemaTimestamp)
        {
            return DataProvider.StartSchemaUpdate_EXPERIMENTAL(schemaTimestamp);
        }
        public static SchemaWriter CreateSchemaWriter()
        {
            return DataProvider.CreateSchemaWriter();
        }
        public static long FinishSchemaUpdate_EXPERIMENTAL(string schemaLock)
        {
            return DataProvider.FinishSchemaUpdate_EXPERIMENTAL(schemaLock);
        }

        #region Backward compatibility

        private static readonly int _contentListStartPage = 10000000;
        internal static readonly int StringPageSize = 80;
        internal static readonly int IntPageSize = 40;
        internal static readonly int DateTimePageSize = 25;
        internal static readonly int CurrencyPageSize = 15;

        public static IDictionary<DataType, int> ContentListMappingOffsets { get; } =
            new ReadOnlyDictionary<DataType, int>(new Dictionary<DataType, int>
        {
            {DataType.String, StringPageSize * _contentListStartPage},
            {DataType.Int, IntPageSize * _contentListStartPage},
            {DataType.DateTime, DateTimePageSize * _contentListStartPage},
            {DataType.Currency, CurrencyPageSize * _contentListStartPage},
            {DataType.Binary, 0},
            {DataType.Reference, 0},
            {DataType.Text, 0}
        });

        #endregion

        /* =============================================================================================== Logging */

        public static async Task WriteAuditEventAsync(AuditEventInfo auditEvent)
        {
            await DataProvider.WriteAuditEventAsync(auditEvent);
        }

        /* =============================================================================================== Tools */

        public static DateTime RoundDateTime(DateTime d)
        {
            return DataProvider.RoundDateTime(d);
        }
        public static bool IsCacheableText(string value)
        {
            return DataProvider.IsCacheableText(value);
        }
        public static Task<string> GetNameOfLastNodeWithNameBaseAsync(int parentId, string namebase, string extension)
        {
            return DataProvider.GetNameOfLastNodeWithNameBaseAsync(parentId, namebase, extension);
        }
        public static Task<long> GetTreeSizeAsync(string path, bool includeChildren)
        {
            return DataProvider.GetTreeSizeAsync(path, includeChildren);
        }
        public static Task<int> GetNodeCountAsync(string path = null)
        {
            return DataProvider.GetNodeCountAsync(path);
        }
        public static Task<int> GetVersionCountAsync(string path = null)
        {
            return DataProvider.GetVersionCountAsync(path);
        }
        public static Task<long> GetNodeTimestampAsync(int nodeId)
        {
            return DataProvider.GetNodeTimestampAsync(nodeId);
        }
        public static Task<long> GetVersionTimestampAsync(int versionId)
        {
            return DataProvider.GetVersionTimestampAsync(versionId);
        }

        public static IMetaQueryEngine MetaQueryEngine { get; } = new NullMetaQueryEngine();

        /* =============================================================================================== */

        private static readonly string NodeDataPrefix = "NodeData.";
        internal static string GenerateNodeDataVersionIdCacheKey(int versionId)
        {
            return string.Concat(NodeDataPrefix, versionId);
        }

        internal static void CacheNodeData(NodeData nodeData, string cacheKey = null)
        {
            if (nodeData == null)
                throw new ArgumentNullException(nameof(nodeData));
            if (cacheKey == null)
                cacheKey = GenerateNodeDataVersionIdCacheKey(nodeData.VersionId);
            var dependency = CacheDependencyFactory.CreateNodeDataDependency(nodeData);
            DistributedApplication.Cache.Insert(cacheKey, nodeData, dependency);
        }
    }
}