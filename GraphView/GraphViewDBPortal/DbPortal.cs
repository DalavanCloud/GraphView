﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Documents.Client;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Newtonsoft.Json.Linq;
using static GraphView.DocumentDBKeywords;

namespace GraphView
{
    internal enum DatabaseType
    {
        DocumentDB,
        JsonServer
    }

    internal class JsonQuery
    {
        public string SelectClause { get; set; }
        public string JoinClause { get; set; }
        public string WhereSearchCondition { get; set; }
        public string Alias { get; set; }

        public List<string> NodeProperties { get; set; } 

        public List<string> EdgeProperties { get; set; }

        public JsonQuery() { }

        public JsonQuery(JsonQuery rhs)
        {
            this.SelectClause = rhs.SelectClause;
            this.JoinClause = rhs.JoinClause;
            this.WhereSearchCondition = rhs.WhereSearchCondition;
            this.Alias = rhs.Alias;
            this.NodeProperties = rhs.NodeProperties;
            this.EdgeProperties = rhs.EdgeProperties;
        }

        public virtual string ToString(DatabaseType dbType)
        {
            switch (dbType) {
            case DatabaseType.DocumentDB:
                return $"SELECT {this.SelectClause} " +
                       $"FROM {this.Alias} " +
                       $"{this.JoinClause} " +
                       $"{(string.IsNullOrEmpty(this.WhereSearchCondition) ? "" : $"WHERE {this.WhereSearchCondition}")}";
            case DatabaseType.JsonServer:
                return $"FOR {this.Alias} IN ('Node') " +
                       $"{(string.IsNullOrEmpty(this.WhereSearchCondition) ? "" : $"WHERE {this.WhereSearchCondition}")}" +
                       $"{this.SelectClause}";
            default:
                throw new NotImplementedException();
            }
        }
    }

    internal class ZQuery : JsonQuery
    {
        public WBooleanExpression RawWhereClause;

        public string NodeAlias;
        public string EdgeAlias;

        public string PartitionKey;

        public HashSet<string> FlatProperties;

        public Dictionary<string, string> JoinDictionary;

        private string zQueryString;

        public ZQuery()
        {
            this.FlatProperties = new HashSet<string>();
            this.JoinDictionary = new Dictionary<string, string>();
        }

        public string ToDocDbString()
        {
            if (this.zQueryString != null)
            {
                return this.zQueryString;
            }
            // construct select clause
            StringBuilder selectStrBuilder = new StringBuilder();
            selectStrBuilder.AppendFormat("SELECT {0}", this.NodeAlias);
            if (this.EdgeAlias != null)
            {
                selectStrBuilder.AppendFormat(", {{\"{0}\": {1}.{0}}} AS {1} ", DocumentDBKeywords.KW_EDGE_ID, this.EdgeAlias);
            }
            string selectClauseString = selectStrBuilder.ToString();


            // cpmstruct FROM clause with the first element of SelectAlias
            StringBuilder fromStrBuilder = new StringBuilder();
            fromStrBuilder.AppendFormat("FROM {0}", this.NodeAlias);
            string fromClauseString = fromStrBuilder.ToString();


            // construct JOIN clause, because the order of replacement is not matter,
            // so use Dictinaty to store it(JoinDictionary).

            // True --> true
            BooleanWValueExpressionVisitor booleanWValueExpressionVisitor = new BooleanWValueExpressionVisitor();
            booleanWValueExpressionVisitor.Invoke(this.RawWhereClause);

            NormalizeNodePredicatesWColumnReferenceExpressionVisitor normalizeNodePredicatesColumnReferenceExpressionVisitor =
                new NormalizeNodePredicatesWColumnReferenceExpressionVisitor(this.PartitionKey);
            normalizeNodePredicatesColumnReferenceExpressionVisitor.AddFlatProperties(this.FlatProperties);
            Dictionary<string, string> referencedProperties =
                normalizeNodePredicatesColumnReferenceExpressionVisitor.Invoke(this.RawWhereClause);
            StringBuilder joinStrBuilder = new StringBuilder();
            foreach (var referencedProperty in referencedProperties)
            {
                joinStrBuilder.AppendFormat(" JOIN {0} IN {1}['{2}'] ", referencedProperty.Key,
                    this.NodeAlias, referencedProperty.Value);
            }

            foreach (KeyValuePair<string, string> pair in JoinDictionary)
            {
                joinStrBuilder.AppendFormat(" JOIN {0} IN {1} ", pair.Key, pair.Value);
            }
            string joinClauseString = joinStrBuilder.ToString();


            // WHERE clause
            // convert some E_6.label --> E_6["label"] if needed(Add 'E_6' to visitor.NeedsConvertion before invoke the visitor).
            
            if (this.EdgeAlias != null)
            {
                DMultiPartIdentifierVisitor normalizeEdgePredicatesColumnReferenceExpressionVisitor = new DMultiPartIdentifierVisitor();
                normalizeEdgePredicatesColumnReferenceExpressionVisitor.NeedsConvertion.Add(this.EdgeAlias);
                normalizeEdgePredicatesColumnReferenceExpressionVisitor.Invoke(this.RawWhereClause);
            }
            

            ToDocDbStringVisitor docDbStringVisitor = new ToDocDbStringVisitor();
            docDbStringVisitor.Invoke(this.RawWhereClause);
            string whereClauseString = $"WHERE ({docDbStringVisitor.GetString()})";

            this.zQueryString = $"{selectClauseString}\n" +
                           $"{fromClauseString} {joinClauseString}\n" +
                           $"{whereClauseString}";

            return this.zQueryString;
        }

        public override string ToString(DatabaseType dbType)
        {
            switch (dbType)
            {
                case DatabaseType.DocumentDB:
                    return this.ToDocDbString();
                case DatabaseType.JsonServer:
                    return $"FOR {this.Alias} IN ('Node') " +
                           $"{(string.IsNullOrEmpty(this.WhereSearchCondition) ? "" : $"WHERE {this.WhereSearchCondition}")}" +
                           $"{this.SelectClause}";
                default:
                    throw new NotImplementedException();
            }
        }
    }

    internal abstract class DbPortal : IDisposable
    {
        public GraphViewConnection Connection { get; protected set; }

        public void Dispose() { }

        public abstract IEnumerator<Tuple<VertexField, RawRecord>> GetVerticesAndEdgesViaVertices(JsonQuery vertexQuery, GraphViewCommand command);

        public abstract IEnumerator<RawRecord> GetVerticesAndEdgesViaEdges(JsonQuery edgeQuery, GraphViewCommand command);

        public abstract List<JObject> GetEdgeDocuments(JsonQuery query);

        public abstract JObject GetEdgeDocument(JsonQuery query);

        public abstract List<VertexField> GetVerticesByIds(HashSet<string> vertexId, GraphViewCommand command);
    }

    internal class DocumentDbPortal : DbPortal
    {
        public DocumentDbPortal(GraphViewConnection connection)
        {
            this.Connection = connection;
        }

        public override IEnumerator<Tuple<VertexField, RawRecord>> GetVerticesAndEdgesViaVertices(JsonQuery vertexQuery, GraphViewCommand command)
        {
            string queryScript = vertexQuery.ToString(DatabaseType.DocumentDB);
            IEnumerable<dynamic> items = this.Connection.ExecuteQuery(queryScript);
            List<string> nodeProperties = new List<string>(vertexQuery.NodeProperties);
            List<string> edgeProperties = new List<string>(vertexQuery.EdgeProperties);

            string nodeAlias = nodeProperties[0];
            // Skip i = 0, which is the (node.* as nodeAlias) field
            nodeProperties.RemoveAt(0);

            //
            // TODO: Refactor
            //
            string edgeAlias = null;
            bool isReverseAdj = false;
            bool isStartVertexTheOriginVertex = false;
            bool crossApplyEdgeOnServer = edgeProperties.Any();
            if (crossApplyEdgeOnServer) {
                edgeAlias = edgeProperties[0];
                isReverseAdj = bool.Parse(edgeProperties[1]);
                isStartVertexTheOriginVertex = bool.Parse(edgeProperties[2]);
                edgeProperties.RemoveAt(0);
                edgeProperties.RemoveAt(0);
                edgeProperties.RemoveAt(0);
            }

            //
            // Batch strategy:
            //  - For "small" vertexes, they have been cross applied on the server side
            //  - For "large" vertexes, just return the VertexField, the adjacency list decoder will
            //    construct spilled adjacency lists in batch mode and cross apply edges after that 
            //
            Func<VertexField, string, Tuple<VertexField, RawRecord>> makeCrossAppliedRecord = (vertexField, edgeId) => {
                Debug.Assert(vertexField != null);

                RawRecord nodeRecord = new RawRecord();
                //
                // Fill node property field
                //
                foreach (string propertyName in nodeProperties) {
                    FieldObject propertyValue = vertexField[propertyName];
                    nodeRecord.Append(propertyValue);
                }

                RawRecord edgeRecord = new RawRecord(edgeProperties.Count);

                EdgeField edgeField =
                    ((AdjacencyListField) vertexField[isReverseAdj ? KW_VERTEX_REV_EDGE : KW_VERTEX_EDGE])
                    .GetEdgeField(edgeId, true);

                string startVertexId = vertexField.VertexId;
                AdjacencyListDecoder.FillMetaField(edgeRecord, edgeField, startVertexId, vertexField.Partition, isStartVertexTheOriginVertex, isReverseAdj);
                AdjacencyListDecoder.FillPropertyField(edgeRecord, edgeField, edgeProperties);

                nodeRecord.Append(edgeRecord);
                return new Tuple<VertexField, RawRecord>(vertexField, nodeRecord);
            };

            Func<VertexField, Tuple<VertexField, RawRecord>> makeRawRecord = (vertexField) => {
                Debug.Assert(vertexField != null);

                RawRecord rawRecord = new RawRecord();
                //
                // Fill node property field
                //
                foreach (string propertyName in nodeProperties)
                {
                    FieldObject propertyValue = vertexField[propertyName];
                    rawRecord.Append(propertyValue);
                }
                return new Tuple<VertexField, RawRecord>(vertexField, rawRecord);
            };

            HashSet<string> uniqueVertexIds = new HashSet<string>();
            HashSet<string> uniqueEdgeIds = new HashSet<string>();
            foreach (dynamic dynamicItem in items) {
                JObject tmpVertexObject = (JObject)((JObject)dynamicItem)[nodeAlias];
                string vertexId = (string)tmpVertexObject[KW_DOC_ID];

                if (crossApplyEdgeOnServer) {
                    // Note: since vertex properties can be multi-valued, 
                    // a DocumentDB query needs a join clause in the FROM clause
                    // to retrieve vertex property values, which may result in 
                    // the same vertex being returned multiple times. 
                    // We use the hash set uniqueVertexIds to ensure one vertex is 
                    // produced only once. 
                    if (EdgeDocumentHelper.IsBuildingTheAdjacencyListLazily(
                            tmpVertexObject, 
                            isReverseAdj, 
                            this.Connection.UseReverseEdges) && 
                            uniqueVertexIds.Add(vertexId))
                    {
                        VertexField vertexField = command.VertexCache.AddOrUpdateVertexField(vertexId, tmpVertexObject);
                        yield return makeRawRecord(vertexField);
                    }
                    else // When the DocumentDB query crosses apply edges 
                    {
                        JObject edgeObjct = (JObject)((JObject)dynamicItem)[edgeAlias];
                        string edgeId = (string)edgeObjct[KW_EDGE_ID];

                        if (uniqueEdgeIds.Add(edgeId)) {
                            VertexField vertexField = command.VertexCache.AddOrUpdateVertexField(vertexId, tmpVertexObject);
                            yield return makeCrossAppliedRecord(vertexField, edgeId);
                        }
                    }
                }
                else
                {
                    if (!uniqueVertexIds.Add(vertexId)) {
                        continue;
                    }
                    VertexField vertexField = command.VertexCache.AddOrUpdateVertexField(vertexId, tmpVertexObject);
                    yield return makeRawRecord(vertexField);
                }
            }
        }

        public override IEnumerator<RawRecord> GetVerticesAndEdgesViaEdges(JsonQuery edgeQuery, GraphViewCommand command)
        {
            string queryScript = edgeQuery.ToString(DatabaseType.DocumentDB);
            IEnumerable<dynamic> items = this.Connection.ExecuteQuery(queryScript);
            List<string> nodeProperties = new List<string>(edgeQuery.NodeProperties);
            List<string> edgeProperties = new List<string>(edgeQuery.EdgeProperties);

            string nodeAlias = nodeProperties[0];
            nodeProperties.RemoveAt(0);

            string edgeAlias = edgeProperties[0];
            edgeProperties.RemoveAt(0);

            HashSet<string> spilledVertexIdSet = new HashSet<string>();
            HashSet<string> spilledVertexPartitionSet = new HashSet<string>();
            //
            // <vertex id, edge id>
            //
            Dictionary<string, List<string>> vertexIdAndEdgeIdsDict = new Dictionary<string, List<string>>();
            //
            // <vertex id, <edgeDocumentId, edgeObject>>
            //
            Dictionary<string, List<Tuple<string, JObject>>> vertexIdAndEdgeObjectsDict =
                new Dictionary<string, List<Tuple<string, JObject>>>();

            foreach (dynamic dynamicItem in items)
            {
                JObject tmpObject = (JObject)((JObject)dynamicItem)[nodeAlias];
                JObject edgeObject = (JObject)((JObject)dynamicItem)[edgeAlias];
                //
                // This is a spilled edge document
                //
                if (tmpObject[KW_EDGEDOC_VERTEXID] != null)
                {
                    string vertexId = tmpObject[KW_EDGEDOC_VERTEXID].ToString();
                    spilledVertexIdSet.Add(vertexId);
                    string partition = this.Connection.GetDocumentPartition(tmpObject);
                    if (partition != null) {
                        spilledVertexPartitionSet.Add(partition);
                    }

                    List<Tuple<string, JObject>> edgeObjects;
                    if (!vertexIdAndEdgeObjectsDict.TryGetValue(vertexId, out edgeObjects))
                    {
                        edgeObjects = new List<Tuple<string, JObject>>();
                        vertexIdAndEdgeObjectsDict.Add(vertexId, edgeObjects);
                    }
                    edgeObjects.Add(new Tuple<string, JObject>((string)tmpObject[KW_DOC_ID], edgeObject));

                    List<string> edgeIds;
                    if (!vertexIdAndEdgeIdsDict.TryGetValue(vertexId, out edgeIds))
                    {
                        edgeIds = new List<string>();
                        vertexIdAndEdgeIdsDict.Add(vertexId, edgeIds);
                    }
                    edgeIds.Add((string)edgeObject[KW_DOC_ID]);
                }
                else
                {
                    string vertexId = (string)tmpObject[KW_DOC_ID];
                    command.VertexCache.AddOrUpdateVertexField(vertexId, tmpObject);
                    List<string> edgeIds;
                    if (!vertexIdAndEdgeIdsDict.TryGetValue(vertexId, out edgeIds))
                    {
                        edgeIds = new List<string>();
                        vertexIdAndEdgeIdsDict.Add(vertexId, edgeIds);
                    }
                    edgeIds.Add((string)edgeObject[KW_DOC_ID]);
                }
            }

            if (spilledVertexIdSet.Any())
            {
                string idInClause = string.Join(", ", spilledVertexIdSet.Select(id => $"'{id}'"));
                string partitionInClause = string.Join(", ", spilledVertexPartitionSet.Select(partition => $"'{partition}'"));
                queryScript = $"SELECT * FROM Node WHERE Node.id IN ({idInClause})" +
                              (string.IsNullOrEmpty(partitionInClause)
                                  ? ""
                                  : $" AND Node{this.Connection.GetPartitionPathIndexer()} IN ({partitionInClause})");
                IEnumerable<dynamic> spilledVertices = this.Connection.ExecuteQuery(queryScript);
                foreach (dynamic vertex in spilledVertices)
                {
                    JObject vertexObject = (JObject)vertex;
                    string vertexId = (string)vertexObject[KW_DOC_ID];
                    VertexField vertexField = command.VertexCache.AddOrUpdateVertexField(vertexId, vertexObject);
                    vertexField.ConstructPartialLazyAdjacencyList(vertexIdAndEdgeObjectsDict[vertexId], false);
                }
            }

            foreach (KeyValuePair<string, List<string>> pair in vertexIdAndEdgeIdsDict)
            {
                string vertexId = pair.Key;
                List<string> edgeIds = pair.Value;
                VertexField vertexField = command.VertexCache.GetVertexField(vertexId);

                foreach (string edgeId in edgeIds)
                {
                    RawRecord nodeRecord = new RawRecord();
                    //
                    // Fill node property field
                    //
                    foreach (string propertyName in nodeProperties)
                    {
                        FieldObject propertyValue = vertexField[propertyName];
                        nodeRecord.Append(propertyValue);
                    }

                    RawRecord edgeRecord = new RawRecord(edgeProperties.Count);

                    EdgeField edgeField = vertexField.AdjacencyList.GetEdgeField(edgeId, false);
                    Debug.Assert(edgeField != null, "edgeField != null");

                    string startVertexId = vertexField.VertexId;
                    AdjacencyListDecoder.FillMetaField(edgeRecord, edgeField, startVertexId, vertexField.Partition, true, false);
                    AdjacencyListDecoder.FillPropertyField(edgeRecord, edgeField, edgeProperties);

                    nodeRecord.Append(edgeRecord);
                    yield return nodeRecord;
                }
            }
        }

        public override List<JObject> GetEdgeDocuments(JsonQuery query)
        {
            string queryScript = query.ToString(DatabaseType.DocumentDB);
            IEnumerable<dynamic> items = this.Connection.ExecuteQuery(queryScript);
            List<JObject> edgeDocuments = new List<JObject>();
            foreach (JObject item in items)
            {
                edgeDocuments.Add(item);
            }
            return edgeDocuments;
        }

        public override JObject GetEdgeDocument(JsonQuery query)
        {
            string queryScript = query.ToString(DatabaseType.DocumentDB);
            return this.Connection.ExecuteQueryUnique(queryScript);
        }


        public override List<VertexField> GetVerticesByIds(HashSet<string> vertexId, GraphViewCommand command)
        {
            string inClause = string.Join(", ", vertexId.Select(x => $"'{x}'"));
            JsonQuery query = new JsonQuery
            {
                SelectClause = "node",
                WhereSearchCondition = "(IS_DEFINED(node._isEdgeDoc) = false AND node.id IN (" + inClause + "))",
                Alias = "node",
                NodeProperties = new List<string> {"node", "*"},
                EdgeProperties = new List<string>()
            };
            IEnumerator<Tuple<VertexField, RawRecord>> queryResult = this.GetVerticesAndEdgesViaVertices(query, command);

            List<VertexField> result = new List<VertexField>();
            while (queryResult.MoveNext())
            {
                VertexField vertex = queryResult.Current.Item1;
                result.Add(vertex);
            }

            EdgeDocumentHelper.ConstructLazyAdjacencyList(command, EdgeType.Both, vertexId, new HashSet<string>());

            return result;
        }
    }
}