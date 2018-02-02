﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Soap;
using System.Text;
using System.Threading.Tasks;
using GraphView.GraphViewDBPortal;

namespace GraphView
{
    internal class GraphViewSerializer
    {
        // When serilize a object like List/Set/Dict, if the object is null, serialize IsNullMark.
        // Use IsNullMark to distinguish whether object is null or empty.
        private const string IsNullMark = "Null";

        public static string SerializeWithDataContract<T>(T data)
        {
            using (MemoryStream memStream = new MemoryStream())
            {
                DataContractSerializer ser = new DataContractSerializer(typeof(T));
                ser.WriteObject(memStream, data);

                memStream.Position = 0;
                StreamReader stringReader = new StreamReader(memStream);
                return stringReader.ReadToEnd();
            }
        }

        public static T DeserializeWithDataContract<T>(string serilizationStr)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                StreamWriter writer = new StreamWriter(stream);
                writer.Write(serilizationStr);
                writer.Flush();
                stream.Position = 0;

                DataContractSerializer deser = new DataContractSerializer(typeof(T));
                return (T)deser.ReadObject(stream);
            }
        }

        private static string SerializeWithSoapFormatter(object obj)
        {
            using (MemoryStream memStream = new MemoryStream())
            {
                SoapFormatter serilizer = new SoapFormatter();
                serilizer.Serialize(memStream, obj);

                memStream.Position = 0;
                StreamReader stringReader = new StreamReader(memStream);
                return stringReader.ReadToEnd();
            }
        }

        private static object DeserializeWithSoapFormatter(string objString, object additionalInfo = null)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                StreamWriter writer = new StreamWriter(stream);
                writer.Write(objString);
                writer.Flush();
                stream.Position = 0;

                SoapFormatter deserilizer;
                if (additionalInfo != null)
                {
                    deserilizer = new SoapFormatter(null, new StreamingContext(StreamingContextStates.All, additionalInfo));
                }
                else
                {
                    deserilizer = new SoapFormatter();
                }

                return deserilizer.Deserialize(stream);
            }
        }

        public static string Serialize(GraphViewCommand command, 
            Dictionary<string, IAggregateFunction> sideEffectFunctions, 
            GraphViewExecutionOperator op)
        {
            string commandString = SerializeWithSoapFormatter(command);

            WrapSideEffectFunctions wrapSideEffectFunctions = new WrapSideEffectFunctions(sideEffectFunctions);
            string sideEffectString = SerializeWithSoapFormatter(wrapSideEffectFunctions);

            string opString = SerializeWithSoapFormatter(op);

            // To combine three string into one string.
            SerializationString serializationString = new SerializationString(commandString, sideEffectString, opString);
            return SerializeWithSoapFormatter(serializationString);
        }

        public static Tuple<GraphViewCommand, GraphViewExecutionOperator> Deserialize(string serializationString, string partitionString)
        {
            SerializationString serializationStringObj = (SerializationString)DeserializeWithSoapFormatter(serializationString);

            GraphViewCommand command = (GraphViewCommand)DeserializeWithSoapFormatter(serializationStringObj.commandString);

            int partitionPlanIndex = int.Parse(Environment.GetEnvironmentVariable("PARTITION_PLAN_INDEX"));
            List<PartitionPlan> partitionPlans = PartitionPlan.DeserializePatitionPlans(partitionString);
            PartitionPlan ownPartitionPlan = partitionPlans[partitionPlanIndex];

            // Deserilization of sideEffectFunctions needs information about command.
            AdditionalSerializationInfo additionalInfo = new AdditionalSerializationInfo(command, partitionPlans, partitionPlanIndex);
            WrapSideEffectFunctions wrapSideEffectFunctions = 
                (WrapSideEffectFunctions) DeserializeWithSoapFormatter(serializationStringObj.sideEffectString, additionalInfo);

            // Deserilization of op needs information about command and sideEffectFunctions.
            additionalInfo.SideEffectFunctions = wrapSideEffectFunctions.sideEffectFunctions;
            GraphViewExecutionOperator op = (GraphViewExecutionOperator)DeserializeWithSoapFormatter(serializationStringObj.opString, additionalInfo);

            GraphViewExecutionOperator firstOp = op.GetFirstOperator();

            FetchNodeOperator fetchNode = firstOp as FetchNodeOperator;
            if (fetchNode != null)
            {
                fetchNode.AppendPartitionPlan(ownPartitionPlan);
                return new Tuple<GraphViewCommand, GraphViewExecutionOperator>(command, op);
            }

            FetchEdgeOperator fetchEdge = firstOp as FetchEdgeOperator;
            if (fetchEdge != null)
            {
                fetchEdge.AppendPartitionPlan(ownPartitionPlan);
                return new Tuple<GraphViewCommand, GraphViewExecutionOperator>(command, op);
            }

            throw new GraphViewException("Can not support this kind of query in parallel mode. First step must be V() or E()");
        }

        

        //
        // Following methods are helper methods. They are only used in the serilization of List/HashSet/Dict. 
        //

        /// <summary>
        /// Return true if there is a string named [name] in [info];
        /// Return false otherwise
        /// </summary>
        /// <param name="info"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        private static bool HasStringValue(SerializationInfo info, string name)
        {
            try
            {
                info.GetValue(name, typeof(string));
                return true;
            }
            catch (Exception e)
            {
                return false;
            }
        }

        public static void SerializeList<T>(SerializationInfo info, string name, List<T> list)
        {
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    info.AddValue($"{name}-{i}", list[i], typeof(T));
                }
            }
            else
            {
                info.AddValue(name, IsNullMark);
            }
        }

        public static List<T> DeserializeList<T>(SerializationInfo info, string name)
        {

            if (HasStringValue(info, name))
            {
                return null;
            }

            List<T> list = new List<T>();
            int index = 0;
            while (true)
            {
                try
                {
                    T item = (T)info.GetValue($"{name}-{index}", typeof(T));
                    list.Add(item);
                    index++;
                }
                catch (SerializationException e)
                {
                    return list;
                }
            }
        }

        public static void SerializeListTuple<T1, T2>(SerializationInfo info, string name, List<Tuple<T1, T2>> list)
        {
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    info.AddValue($"{name}-item1-{i}", list[i].Item1, typeof(T1));
                    info.AddValue($"{name}-item2-{i}", list[i].Item2, typeof(T2));
                }
            }
            else
            {
                info.AddValue(name, IsNullMark);
            }
        }

        public static List<Tuple<T1, T2>> DeserializeListTuple<T1, T2>(SerializationInfo info, string name)
        {
            if (HasStringValue(info, name))
            {
                return null;
            }

            List<Tuple<T1, T2>> list = new List<Tuple<T1, T2>>();
            int index = 0;
            while (true)
            {
                try
                {
                    T1 item1 = (T1)info.GetValue($"{name}-item1-{index}", typeof(T1));
                    T2 item2 = (T2)info.GetValue($"{name}-item2-{index}", typeof(T2));
                    list.Add(new Tuple<T1, T2>(item1, item2));
                    index++;
                }
                catch (SerializationException e)
                {
                    return list;
                }
            }
        }

        public static void SerializeListTupleList<T1, T2>(SerializationInfo info, string name, List<Tuple<T1, List<T2>>> list)
        {
            if (list != null)
            {
                info.AddValue($"{name}-Count", list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    info.AddValue($"{name}-item1-{i}", list[i].Item1, typeof(T1));
                    SerializeList(info, $"{name}-item2-{i}", list[i].Item2);
                }
            }
            else
            {
                info.AddValue(name, IsNullMark);
            }
        }

        public static List<Tuple<T1, List<T2>>> DeserializeListTupleList<T1, T2>(SerializationInfo info, string name)
        {
            if (HasStringValue(info, name))
            {
                return null;
            }

            List<Tuple<T1, List<T2>>> list = new List<Tuple<T1, List<T2>>>();
            int count = info.GetInt32($"{name}-Count");
            for (int i = 0; i < count; i++)
            {
                T1 item1 = (T1)info.GetValue($"{name}-item1-{i}", typeof(T1));
                List<T2> item2 = DeserializeList<T2>(info, $"{name}-item2-{i}");
                list.Add(new Tuple<T1, List<T2>>(item1, item2));
            }
            return list;
        }

        public static void SerializeHashSet<T>(SerializationInfo info, string name, HashSet<T> set)
        {
            if (set != null)
            {
                int index = 0;
                foreach (T item in set)
                {
                    info.AddValue($"{name}-{index}", item, typeof(T));
                    index++;
                }
            }
            else
            {
                info.AddValue(name, IsNullMark);
            }
        }

        public static HashSet<T> DeserializeHashSet<T>(SerializationInfo info, string name)
        {
            if (HasStringValue(info, name))
            {
                return null;
            }

            HashSet<T> set = new HashSet<T>();
            int index = 0;
            while (true)
            {
                try
                {
                    T item = (T)info.GetValue($"{name}-{index}", typeof(T));
                    set.Add(item);
                    index++;
                }
                catch (SerializationException e)
                {
                    return set;
                }
            }
        }

        public static void SerializeListHashSet<T>(SerializationInfo info, string name, List<HashSet<T>> list)
        {
            if (list != null)
            {
                info.AddValue($"{name}-Count", list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    SerializeHashSet(info, $"{name}-{i}", list[i]);
                }
            }
            else
            {
                info.AddValue(name, IsNullMark);
            }
        }

        public static List<HashSet<T>> DeserializeListHashSet<T>(SerializationInfo info, string name)
        {
            if (HasStringValue(info, name))
            {
                return null;
            }

            List<HashSet<T>> list = new List<HashSet<T>>();
            int count = info.GetInt32($"{name}-Count");
            for (int i = 0; i < count; i++)
            {
                HashSet<T> item = DeserializeHashSet<T>(info, $"{name}-{i}");
                list.Add(item);
            }
            return list;
        }

        public static void SerializeListTupleHashSet<T1, T2, T3>(SerializationInfo info, string name, List<Tuple<T1, T2, HashSet<T3>>> list)
        {
            if (list != null)
            {
                info.AddValue($"{name}-Count", list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    info.AddValue($"{name}-item1-{i}", list[i].Item1, typeof(T1));
                    info.AddValue($"{name}-item2-{i}", list[i].Item2, typeof(T2));
                    SerializeHashSet(info, $"{name}-item3-{i}", list[i].Item3);
                }
            }
            else
            {
                info.AddValue(name, IsNullMark);
            }
        }

        public static List<Tuple<T1, T2, HashSet<T3>>> DeserializeListTupleHashSet<T1, T2, T3>(SerializationInfo info, string name)
        {
            if (HasStringValue(info, name))
            {
                return null;
            }

            List<Tuple<T1, T2, HashSet<T3>>> list = new List<Tuple<T1, T2, HashSet<T3>>>();
            int count = info.GetInt32($"{name}-Count");
            for (int i = 0; i < count; i++)
            {
                T1 item1 = (T1)info.GetValue($"{name}-item1-{i}", typeof(T1));
                T2 item2 = (T2)info.GetValue($"{name}-item2-{i}", typeof(T2));
                HashSet<T3> item3 = DeserializeHashSet<T3>(info, $"{name}-item3-{i}");
                list.Add(new Tuple<T1, T2, HashSet<T3>>(item1, item2, item3));
            }
            return list;
        }

        public static void SerializeDictionary<T1, T2>(SerializationInfo info, string name, Dictionary<T1, T2> dict)
        {
            if (dict != null)
            {
                int index = 0;
                foreach (KeyValuePair<T1, T2> pair in dict)
                {
                    info.AddValue($"{name}-key-{index}", pair.Key, typeof(T1));
                    info.AddValue($"{name}-value-{index}", pair.Value, typeof(T2));
                    index++;
                }
            }
            else
            {
                info.AddValue(name, IsNullMark);
            }
        }

        public static Dictionary<T1, T2> DeserializeDictionary<T1, T2>(SerializationInfo info, string name)
        {
            if (HasStringValue(info, name))
            {
                return null;
            }

            Dictionary<T1, T2> dict = new Dictionary<T1, T2>();
            int index = 0;
            while (true)
            {
                try
                {
                    T1 key = (T1) info.GetValue($"{name}-key-{index}", typeof(T1));
                    T2 value = (T2) info.GetValue($"{name}-value-{index}", typeof(T2));
                    dict.Add(key, value);
                    index++;
                }
                catch (SerializationException e)
                {
                    return dict;
                }
            }
        }

        public static void SerializeDictionaryTuple<T1, T2, T3>(SerializationInfo info, string name, Dictionary<T1, Tuple<T2, T3>> dict)
        {
            if (dict != null)
            {
                int index = 0;
                foreach (KeyValuePair<T1, Tuple<T2, T3>> pair in dict)
                {
                    info.AddValue($"{name}-key-{index}", pair.Key, typeof(T1));
                    info.AddValue($"{name}-value1-{index}", pair.Value.Item1, typeof(T2));
                    info.AddValue($"{name}-value2-{index}", pair.Value.Item2, typeof(T3));
                    index++;
                }
            }
            else
            {
                info.AddValue(name, IsNullMark);
            }
        }

        public static Dictionary<T1, Tuple<T2, T3>> DeserializeDictionaryTuple<T1, T2, T3>(SerializationInfo info, string name)
        {
            if (HasStringValue(info, name))
            {
                return null;
            }

            Dictionary<T1, Tuple<T2, T3>> dict = new Dictionary<T1, Tuple<T2, T3>>();
            int index = 0;
            while (true)
            {
                try
                {
                    T1 key = (T1)info.GetValue($"{name}-key-{index}", typeof(T1));
                    T2 value1 = (T2)info.GetValue($"{name}-value1-{index}", typeof(T2));
                    T3 value2 = (T3)info.GetValue($"{name}-value2-{index}", typeof(T3));
                    dict.Add(key, new Tuple<T2, T3>(value1, value2));
                    index++;
                }
                catch (SerializationException e)
                {
                    return dict;
                }
            }
        }

        public static void SerializeDictionaryList<T1, T2>(SerializationInfo info, string name, Dictionary<T1, List<T2>> dict)
        {
            if (dict != null)
            {
                int index = 0;
                foreach (KeyValuePair<T1, List<T2>> pair in dict)
                {
                    info.AddValue($"{name}-key-{index}", pair.Key, typeof(T1));
                    SerializeList(info, $"{name}-value-{index}", pair.Value);
                    index++;
                }
            }
            else
            {
                info.AddValue(name, IsNullMark);
            }
        }

        public static Dictionary<T1, List<T2>> DeserializeDictionaryList<T1, T2>(SerializationInfo info, string name, bool valueIsList)
        {
            if (HasStringValue(info, name))
            {
                return null;
            }

            Dictionary<T1, List<T2>> dict = new Dictionary<T1, List<T2>>();
            int index = 0;
            while (true)
            {
                try
                {
                    T1 key = (T1)info.GetValue($"{name}-key-{index}", typeof(T1));
                    List<T2> value = DeserializeList<T2>(info, $"{name}-value-{index}");
                    dict.Add(key, value);
                    index++;
                }
                catch (SerializationException e)
                {
                    return dict;
                }
            }
        }
    }

    /// <summary>
    /// Wrap sideEffectFunctions into a object.
    /// Make the serialization and deserialization of sideEffectFunctions convenient.
    /// </summary>
    [Serializable]
    internal class WrapSideEffectFunctions : ISerializable
    {
        public Dictionary<string, IAggregateFunction> sideEffectFunctions;

        public WrapSideEffectFunctions(Dictionary<string, IAggregateFunction> sideEffectFunctions)
        {
            this.sideEffectFunctions = sideEffectFunctions;
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            GraphViewSerializer.SerializeDictionary(info, "sideEffectFunctions", this.sideEffectFunctions);
        }

        protected WrapSideEffectFunctions(SerializationInfo info, StreamingContext context)
        {
            Debug.Assert((context.Context as AdditionalSerializationInfo)?.Command != null);

            this.sideEffectFunctions = GraphViewSerializer.DeserializeDictionary<string, IAggregateFunction>(info, "sideEffectFunctions");
        }
    }

    /// <summary>
    /// To store addtional info about Command and SideEffectFunctions.
    /// These info will be used in deserialization.
    /// </summary>
    internal class AdditionalSerializationInfo
    {
        public GraphViewCommand Command { get; private set; }
        public List<PartitionPlan> PartitionPlans { get; private set; }
        public int TaskIndex { get; private set; }
        public Dictionary<string, IAggregateFunction> SideEffectFunctions { get; set; }

        public AdditionalSerializationInfo(GraphViewCommand command, List<PartitionPlan> partitionPlans, int taskIndex)
        {
            this.Command = command;
            this.PartitionPlans = partitionPlans;
            this.TaskIndex = taskIndex;
        }
    }

    public enum PartitionCompareType
    {
        In,
        Between
    }

    public enum PartitionBetweenType
    {
        IncludeBoth, // a <= x <= b
        IncludeLeft, // a <= x < b
        IncludeRight, // a < x <=b
        Greater, // a < x
        GreaterOrEqual, // a <= x
        Less, // x < b
        LessOrEqual // x <= b
    }

    public enum PartitionMethod
    {
        CompareEntire, // compare entire partition key.
        CompareFirstChar, // compare the first char of the partition key.
    }

    [DataContract]
    public class PartitionPlan
    {
        [DataMember]
        private string partitionKey;

        [DataMember]
        private PartitionCompareType compareType;

        [DataMember]
        private List<string> inValues;
        [DataMember]
        private Tuple<string, string, PartitionBetweenType> betweenValues;

        [DataMember]
        private PartitionMethod partitionMethod;

        [DataMember]
        public string IP { get; private set; }
        [DataMember]
        public int Port { get; private set; }

        public PartitionPlan(string partitionKey, PartitionMethod partitionMethod, string ip, int port)
        {
            this.partitionKey = partitionKey;
            this.partitionMethod = partitionMethod;
            this.IP = ip;
            this.Port = port;
        }

        public PartitionPlan(string partitionKey, PartitionMethod partitionMethod, string ip, int port, List<string> inValues) 
            : this(partitionKey, partitionMethod, ip, port)
        {
            this.inValues = inValues;
            this.compareType = PartitionCompareType.In;
        }

        public PartitionPlan(string partitionKey, PartitionMethod partitionMethod, string ip, int port, Tuple<string, string, PartitionBetweenType> betweenValues)
            : this(partitionKey, partitionMethod, ip, port)
        {
            this.betweenValues = betweenValues;
            this.compareType = PartitionCompareType.Between;
        }

        internal void AppendToWhereClause(JsonQuery jsonQuery)
        {
            if (this.partitionMethod == PartitionMethod.CompareEntire)
            {
                if (this.compareType == PartitionCompareType.In)
                {
                    jsonQuery.FlatProperties.Add(this.partitionKey);
                    jsonQuery.WhereConjunction(new WInPredicate(
                        new WColumnReferenceExpression(jsonQuery.NodeAlias ?? jsonQuery.EdgeAlias, this.partitionKey), this.inValues),
                        BooleanBinaryExpressionType.And);
                }
                else
                {
                    throw new NotImplementedException();
                }
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Deprecated. Will be deleted later.
        /// </summary>
        /// <param name="tableAlias"></param>
        /// <param name="whereClause"></param>
        /// <returns></returns>
        internal string AppendToWhereClause(string tableAlias, string whereClause)
        {
            if (this.partitionMethod == PartitionMethod.CompareEntire)
            {
                if (this.compareType == PartitionCompareType.In)
                {
                    throw new NotImplementedException();
                }
                else
                {
                    throw new NotImplementedException();
                }
            }
            else if (this.partitionMethod == PartitionMethod.CompareFirstChar)
            {
                if (this.compareType == PartitionCompareType.Between)
                {
                    string leftSymbol = "<";
                    string rightSymbol = "<";
                    switch (this.betweenValues.Item3)
                    {
                        case PartitionBetweenType.IncludeBoth:
                            leftSymbol += "=";
                            rightSymbol += "=";
                            break;
                        case PartitionBetweenType.IncludeLeft:
                            leftSymbol += "=";
                            break;
                        case PartitionBetweenType.IncludeRight:
                            rightSymbol += "=";
                            break;
                        case PartitionBetweenType.GreaterOrEqual:
                            leftSymbol += "=";
                            break;
                        case PartitionBetweenType.LessOrEqual:
                            rightSymbol += "=";
                            break;
                    }

                    switch (this.betweenValues.Item3)
                    {
                        case PartitionBetweenType.IncludeBoth:
                        case PartitionBetweenType.IncludeLeft:
                        case PartitionBetweenType.IncludeRight:
                            return $"({whereClause}) AND " +
                                   $"\"{this.betweenValues.Item1}\" {leftSymbol} LOWER(LEFT({tableAlias}.{this.partitionKey}, 1)) " +
                                   $"AND LOWER(LEFT({tableAlias}.{this.partitionKey}, 1)) {rightSymbol} \"{this.betweenValues.Item2}\" ";
                        case PartitionBetweenType.Greater:
                        case PartitionBetweenType.GreaterOrEqual:
                            return $"({whereClause}) AND " +
                                   $"\"{this.betweenValues.Item1}\" {leftSymbol} LOWER(LEFT({tableAlias}.{this.partitionKey}, 1)) ";
                        case PartitionBetweenType.Less:
                        case PartitionBetweenType.LessOrEqual:
                            return $"({whereClause}) AND " +
                                   $"LOWER(LEFT({tableAlias}.{this.partitionKey}, 1)) {rightSymbol} \"{this.betweenValues.Item2}\" ";
                        default:
                            throw new NotImplementedException();
                    }
                }
                else
                {
                    throw new NotImplementedException();
                }
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        internal bool BelongToPartitionPlan(string partition)
        {
            if (this.partitionMethod == PartitionMethod.CompareEntire)
            {
                if (this.compareType == PartitionCompareType.In)
                {
                    foreach (string inValue in this.inValues)
                    {
                        if (partition == inValue)
                        {
                            return true;
                        }
                    }
                    return false;
                }
                else
                {
                    throw new NotImplementedException();
                }
            }
            else if (this.partitionMethod == PartitionMethod.CompareFirstChar)
            {
                throw new NotImplementedException();
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        public static string SerializePatitionPlans(List<PartitionPlan> partitionPlans)
        {
            using (MemoryStream memStream = new MemoryStream())
            {
                DataContractSerializer ser = new DataContractSerializer(typeof(List<PartitionPlan>));
                ser.WriteObject(memStream, partitionPlans);

                memStream.Position = 0;
                StreamReader stringReader = new StreamReader(memStream);
                return stringReader.ReadToEnd();
            }
        }

        public static List<PartitionPlan> DeserializePatitionPlans(string partitionPlanStr)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                StreamWriter writer = new StreamWriter(stream);
                writer.Write(partitionPlanStr);
                writer.Flush();
                stream.Position = 0;

                DataContractSerializer deser = new DataContractSerializer(typeof(List<PartitionPlan>));
                List<PartitionPlan> partitionPlans = (List<PartitionPlan>)deser.ReadObject(stream);
                return partitionPlans;
            }

        }
    }

    [Serializable]
    public class SerializationString
    {
        public string commandString;
        public string sideEffectString;
        public string opString;

        public SerializationString(string commandString, string sideEffectString, string opString)
        {
            this.commandString = commandString;
            this.sideEffectString = sideEffectString;
            this.opString = opString;
        }
    }
}
