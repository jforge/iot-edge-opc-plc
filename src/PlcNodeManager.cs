namespace OpcPlc;

using Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using Opc.Ua;
using Opc.Ua.Server;
using PluginNodes.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;

public class PlcNodeManager : CustomNodeManager2
{
    private readonly OpcPlcConfiguration _config;
    private readonly ILogger _logger;
    private readonly ImmutableList<IPluginNodes> _pluginNodes;

    private readonly TimeService _timeService;
    private IDictionary<NodeId, IList<IReference>> _externalReferences;
    private Func<ISystemContext, NodeStateCollection> _loadPredefinedNodesHandler;
    private IMqttClient _mqttClient;
    private IMqttClientOptions _mqttOptions;

    public PlcNodeManager(IServerInternal server, OpcPlcConfiguration config, ApplicationConfiguration appConfig,
        TimeService timeService, PlcSimulation plcSimulation, ImmutableList<IPluginNodes> pluginNodes, ILogger logger)
        : base(server, appConfig, Namespaces.OpcPlcApplications, Namespaces.OpcPlcBoiler,
            Namespaces.OpcPlcBoilerInstance)
    {
        _config = config;
        _timeService = timeService;
        PlcSimulationInstance = plcSimulation;
        _pluginNodes = pluginNodes;
        _logger = logger;
        SystemContext.NodeIdFactory = this;
    }

    public PlcSimulation PlcSimulationInstance { get; }

    /// <summary>
    ///     Creates the NodeId for the specified node.
    /// </summary>
    public override NodeId New(ISystemContext context, NodeState node)
    {
        return node is BaseInstanceState { Parent: not null } instance &&
               instance.Parent.NodeId.Identifier is string id
            ? new NodeId(id + "_" + instance.SymbolicName, instance.Parent.NodeId.NamespaceIndex)
            : node.NodeId;
    }

    /// <summary>
    ///     Does any initialization required before the address space can be used.
    /// </summary>
    /// <remarks>
    ///     The externalReferences is an out parameter that allows the node manager to link to nodes
    ///     in other node managers. For example, the 'Objects' node is managed by the CoreNodeManager and
    ///     should have a reference to the root folder node(s) exposed by this node manager.
    /// </remarks>
    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            }

            _externalReferences = externalReferences;

            FolderState root = CreateFolder(null, _config.ProgramName, _config.ProgramName,
                NamespaceType.OpcPlcApplications);
            root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));
            root.EventNotifier = EventNotifiers.SubscribeToEvents;
            AddRootNotifier(root);

            try
            {
                FolderState telemetryFolder =
                    CreateFolder(root, "Telemetry", "Telemetry", NamespaceType.OpcPlcApplications);
                FolderState methodsFolder = CreateFolder(root, "Methods", "Methods", NamespaceType.OpcPlcApplications);

                // Add nodes to address space from plugin nodes list.
                foreach (IPluginNodes plugin in _pluginNodes)
                {
                    plugin.AddToAddressSpace(telemetryFolder, methodsFolder, this);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error creating address space");
            }

            AddPredefinedNode(SystemContext, root);

            // Initialize MQTT client
            InitializeMqttClient("test.mosquitto.org");

            // Register the custom method node
            CreateCustomMethodNode();
        }
    }

    private void InitializeMqttClient(string brokerAddress)
    {
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        _mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(brokerAddress)
            .Build();
    }

    private void CreateCustomMethodNode()
    {
        // Define the method node
        var methodNode = new MethodState(null) {
            NodeId = new NodeId("SendMqttMessages", NamespaceIndex),
            BrowseName = new QualifiedName("SendMqttMessages", NamespaceIndex),
            DisplayName = new LocalizedText("SendMqttMessages"),
            Description = new LocalizedText("Send a specified number of MQTT messages."),
            Executable = true,
            UserExecutable = true
        };

        // Bind method event
        methodNode.OnCallMethod = OnSendMqttMessages;

        // Add the method node to the server
        AddPredefinedNode(SystemContext, methodNode);
    }

    private ServiceResult OnSendMqttMessages(
        ISystemContext context,
        MethodState method,
        IList<object> inputArguments,
        IList<object> outputArguments)
    {
        if (inputArguments.Count >= 2 && inputArguments[0] is uint numberOfMessages &&
            inputArguments[1] is uint sleepDurationMs)
        {
            Task.Run(async () => {
                try
                {
                    if (!_mqttClient.IsConnected)
                    {
                        await _mqttClient.ConnectAsync(_mqttOptions).ConfigureAwait(false); // No context capturing
                    }

                    for (uint i = 0; i < numberOfMessages; i++)
                    {
                        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
                            .WithTopic("opcua/messages")
                            .WithPayload($"Message {i + 1}")
                            .Build();

                        await _mqttClient.PublishAsync(message).ConfigureAwait(false); // No context capturing
                        await Task.Delay((int)sleepDurationMs).ConfigureAwait(false); // No context capturing
                    }

                    outputArguments[0] = "MQTT messages sent successfully.";
                }
                catch (Exception ex)
                {
                    outputArguments[0] = $"Error sending MQTT messages: {ex.Message}";
                }
            });

            return ServiceResult.Good;
        }

        // If the input arguments are not valid, return a "BadInvalidArgument" status.
        return new ServiceResult(StatusCodes.BadInvalidArgument);
    }

    public SimulatedVariableNode<T> CreateVariableNode<T>(BaseDataVariableState variable)
    {
        return new SimulatedVariableNode<T>(SystemContext, variable, _timeService);
    }

    /// <summary>
    ///     Creates a new folder.
    /// </summary>
    public FolderState CreateFolder(NodeState parent, string path, string name, NamespaceType namespaceType)
    {
        BaseInstanceState existingFolder = parent?.FindChildBySymbolicName(SystemContext, name);
        if (existingFolder != null)
        {
            return (FolderState)existingFolder;
        }

        ushort namespaceIndex = NamespaceIndexes[(int)namespaceType];

        var folder = new FolderState(parent) {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId(path, namespaceIndex),
            BrowseName = new QualifiedName(path, namespaceIndex),
            DisplayName = new LocalizedText("en", name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None
        };

        parent?.AddChild(folder);

        return folder;
    }

    /// <summary>
    ///     Creates a new extended variable.
    /// </summary>
    public BaseDataVariableState CreateBaseVariable(NodeState parent, dynamic path, string name, NodeId dataType,
        int valueRank, byte accessLevel, string description, NamespaceType namespaceType, bool randomize,
        object stepSizeValue, object minTypeValue, object maxTypeValue, object defaultValue = null)
    {
        var baseDataVariableState =
            new BaseDataVariableStateExtended(parent, randomize, stepSizeValue, minTypeValue, maxTypeValue) {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType
            };

        return CreateBaseVariable(baseDataVariableState, parent, path, name, dataType, valueRank, accessLevel,
            description, namespaceType, defaultValue);
    }

    /// <summary>
    ///     Creates a new variable.
    /// </summary>
    public BaseDataVariableState CreateBaseVariable(NodeState parent, dynamic path, string name, NodeId dataType,
        int valueRank, byte accessLevel, string description, NamespaceType namespaceType, object defaultValue = null)
    {
        var baseDataVariableState = new BaseDataVariableState(parent) {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType
        };

        return CreateBaseVariable(baseDataVariableState, parent, path, name, dataType, valueRank, accessLevel,
            description, namespaceType, defaultValue);
    }

    /// <summary>
    ///     Creates a new method.
    /// </summary>
    public MethodState CreateMethod(NodeState parent, string path, string name, string description,
        NamespaceType namespaceType)
    {
        ushort namespaceIndex = NamespaceIndexes[(int)namespaceType];

        var method = new MethodState(parent) {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            NodeId = new NodeId(path, namespaceIndex),
            BrowseName = new QualifiedName(path, namespaceIndex),
            DisplayName = new LocalizedText("en", name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            Executable = true,
            UserExecutable = true,
            Description = new LocalizedText(description)
        };

        parent?.AddChild(method);

        return method;
    }

    private BaseDataVariableState CreateBaseVariable(BaseDataVariableState baseDataVariableState, NodeState parent,
        dynamic path, string name, NodeId dataType, int valueRank, byte accessLevel, string description,
        NamespaceType namespaceType, object defaultValue = null)
    {
        ushort namespaceIndex = NamespaceIndexes[(int)namespaceType];

        if (path is uint or long)
        {
            baseDataVariableState.NodeId = new NodeId((uint)path, namespaceIndex);
            baseDataVariableState.BrowseName = new QualifiedName(((uint)path).ToString(), namespaceIndex);
        }
        else if (path is string)
        {
            baseDataVariableState.NodeId = new NodeId(path, namespaceIndex);
            baseDataVariableState.BrowseName = new QualifiedName(path, namespaceIndex);
        }
        else
        {
            _logger.LogDebug("NodeId type is {NodeIdType}", (string)path.GetType().ToString());
            baseDataVariableState.NodeId = new NodeId(path, namespaceIndex);
            baseDataVariableState.BrowseName = new QualifiedName(name, namespaceIndex);
        }

        baseDataVariableState.DisplayName = new LocalizedText("en", name);
        baseDataVariableState.WriteMask = AttributeWriteMask.DisplayName | AttributeWriteMask.Description;
        baseDataVariableState.UserWriteMask = AttributeWriteMask.DisplayName | AttributeWriteMask.Description;
        baseDataVariableState.DataType = dataType;
        baseDataVariableState.ValueRank = valueRank;
        baseDataVariableState.AccessLevel = accessLevel;
        baseDataVariableState.UserAccessLevel = accessLevel;
        baseDataVariableState.Historizing = false;
        baseDataVariableState.Value = defaultValue ?? TypeInfo.GetDefaultValue(dataType, valueRank, Server.TypeTree);
        baseDataVariableState.StatusCode = StatusCodes.Good;
        baseDataVariableState.Timestamp = _timeService.UtcNow();
        baseDataVariableState.Description = new LocalizedText(description);

        if (valueRank == ValueRanks.OneDimension)
        {
            baseDataVariableState.ArrayDimensions = new ReadOnlyList<uint>(new List<uint> { 0 });
        }
        else if (valueRank == ValueRanks.TwoDimensions)
        {
            baseDataVariableState.ArrayDimensions = new ReadOnlyList<uint>(new List<uint> { 0, 0 });
        }

        parent?.AddChild(baseDataVariableState);

        return baseDataVariableState;
    }

    /// <summary>
    ///     Loads a predefined node set by using the specified handler.
    /// </summary>
    public void LoadPredefinedNodes(Func<ISystemContext, NodeStateCollection> loadPredefinedNodesHandler)
    {
        _loadPredefinedNodesHandler = loadPredefinedNodesHandler;

        base.LoadPredefinedNodes(SystemContext, _externalReferences);
    }

    /// <summary>
    ///     Adds a predefined node set.
    /// </summary>
    public void AddPredefinedNode(NodeState node)
    {
        base.AddPredefinedNode(SystemContext, node);
    }

    protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
    {
        return _loadPredefinedNodesHandler?.Invoke(context);
    }
}
