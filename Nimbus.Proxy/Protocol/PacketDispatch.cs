namespace Nimbus.Proxy;

// Maps the protobuf "first-field tag" of a VS packet frame to a human-readable name.
// Tag = (fieldNumber << 3) | wireType. Pulled from Packet_ClientSerializer.Serialize and
// Packet_ServerSerializer.Serialize in VintagestoryLib.
internal static class PacketDispatch
{
    // Client -> Server. Tag 8 ('Id') is the bare-discriminator used by simple packets with no body.
    // For those, the next bytes decode to one of Packet_ClientIdEnum (PingReply, RequestJoin, etc).
    public static readonly Dictionary<int, string> ClientTags = new()
    {
        { 8,   "Id" },                  // varint, next = Packet_ClientIdEnum value
        { 266, "LoginTokenQuery" },     // field 33
        { 18,  "Identification" },      // field 2
        { 26,  "BlockPlaceOrBreak" },   // field 3
        { 34,  "Chatline" },            // field 4
        { 42,  "RequestJoin" },         // field 5
        { 50,  "PingReply" },           // field 6
        { 58,  "SpecialKey" },          // field 7
        { 66,  "SelectedHotbarSlot" },  // field 8
        { 74,  "Leave" },               // field 9
        { 82,  "ServerQuery" },         // field 10
        { 114, "MoveItemstack" },       // field 14
        { 122, "FlipItemstacks" },      // field 15
        { 130, "EntityInteraction" },   // field 16
        { 146, "EntityPosition" },      // field 18
        { 154, "ActivateInventorySlot"},// field 19
        { 162, "CreateItemstack" },     // field 20
        { 170, "RequestModeChange" },   // field 21
        { 178, "MoveKeyChange" },       // field 22
        { 186, "BlockEntityPacket" },   // field 23
        { 194, "CustomPacket" },        // field 24
        { 202, "HandInteraction" },     // field 25
        { 210, "ToolMode" },            // field 26
        { 218, "BlockDamage" },         // field 27
        { 226, "ClientPlaying" },       // field 28
        { 242, "InvOpenedClosed" },     // field 30
        { 250, "EntityPacket" },        // field 31
        { 258, "RuntimeSetting" },      // field 32
        { 274, "UdpPacket" },           // field 34
    };

    // Server -> Client.
    public static readonly Dictionary<int, string> ServerTags = new()
    {
        { 720, "Id" },                  // varint discriminator (Packet_ServerIdEnum)
        { 618, "TokenAnswer" },         // field 77
        { 10,  "Identification" },      // field 1 (ServerIdentification)
        { 18,  "LevelInitialize" },     // field 2
        { 26,  "LevelDataChunk" },      // field 3
        { 34,  "LevelFinalize" },       // field 4
        { 42,  "SetBlock" },            // field 5
        { 58,  "Chatline" },            // field 7
        { 66,  "DisconnectPlayer" },    // field 8
        { 74,  "Chunks" },              // field 9
        { 82,  "UnloadChunk" },         // field 10
        { 90,  "Calendar" },            // field 11
        { 122, "MapChunk" },            // field 15
        { 130, "Ping" },                // field 16
        { 138, "PlayerPing" },          // field 17
        { 146, "Sound" },               // field 18
        { 154, "Assets" },              // field 19
        { 170, "WorldMetaData" },       // field 21
        { 226, "QueryAnswer" },         // field 28
        { 234, "Redirect" },            // field 29
        { 242, "InventoryContents" },   // field 30
        { 250, "InventoryUpdate" },     // field 31
        { 258, "InventoryDoubleUpdate" },// field 32
        { 274, "Entity" },              // field 34
        { 282, "EntitySpawn" },         // field 35
        { 290, "EntityDespawn" },       // field 36
        { 306, "EntityAttributes" },    // field 38
        { 314, "EntityAttributeUpdate"},// field 39
        { 322, "Entities" },            // field 40
        { 330, "PlayerData" },          // field 41
        { 338, "MapRegion" },           // field 42
        { 354, "BlockEntityMessage" },  // field 44
        { 362, "PlayerDeath" },         // field 45
        { 370, "ModeChange" },          // field 46
        { 378, "SetBlocks" },           // field 47
        { 386, "BlockEntities" },       // field 48
        { 394, "PlayerGroups" },        // field 49
        { 402, "PlayerGroup" },         // field 50
        { 410, "EntityPosition" },      // field 51
        { 418, "HighlightBlocks" },     // field 52
        { 426, "SelectedHotbarSlot" },  // field 53
        { 442, "CustomPacket" },        // field 55
        { 450, "NetworkChannels" },     // field 56
        { 458, "GotoGroup" },           // field 57
        { 466, "ExchangeBlock" },       // field 58
        { 474, "BulkEntityAttributes" },// field 59
        { 482, "SpawnParticles" },      // field 60
        { 490, "BulkEntityDebugAttributes" },// field 61
        { 498, "SetBlocksNoRelight" },  // field 62
        { 514, "BlockDamage" },         // field 64
        { 522, "Ambient" },             // field 65
        { 530, "NotifySlot" },          // field 66
        { 538, "EntityPacket" },        // field 67
        { 546, "IngameError" },         // field 68
        { 554, "IngameDiscovery" },     // field 69
        { 562, "SetBlocksMinimal" },    // field 70
        { 570, "SetDecors" },           // field 71
        { 578, "RemoveBlockLight" },    // field 72
        { 586, "ServerReady" },         // field 73
        { 594, "UnloadMapRegion" },     // field 74
        { 602, "LandClaims" },          // field 75
        { 610, "Roles" },               // field 76
        { 626, "UdpPacket" },           // field 78
        { 634, "QueuePacket" },         // field 79
        { 666, "Calendarupdate" },      // field 83
    };

    /// <summary>
    /// Reads a protobuf varint from <paramref name="payload"/> starting at <paramref name="offset"/>.
    /// Returns the decoded value and advances <paramref name="offset"/>. Returns -1 if truncated.
    /// </summary>
    public static int ReadVarint(ReadOnlySpan<byte> payload, ref int offset)
    {
        int result = 0;
        int shift = 0;
        while (offset < payload.Length)
        {
            byte b = payload[offset++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 35) return -1; // malformed
        }
        return -1;
    }

    public static string Describe(bool clientToServer, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return "<empty>";
        int off = 0;
        int tag = ReadVarint(payload, ref off);
        if (tag < 0) return "<truncated>";

        var table = clientToServer ? ClientTags : ServerTags;
        bool isBareId = clientToServer ? tag == 8 : tag == 720;

        if (isBareId)
        {
            int id = ReadVarint(payload, ref off);
            if (id < 0) return "Id?";
            string idName = clientToServer ? ClientIdName(id) : ServerIdName(id);
            return $"Id={id}({idName})";
        }

        return table.TryGetValue(tag, out var name) ? name : $"tag{tag}";
    }

    // Only the packet IDs useful for handshake and transfer tracing.
    private static string ClientIdName(int id) => id switch
    {
        1  => "PlayerIdentification",
        2  => "PingReply",
        11 => "RequestJoin",
        14 => "Leave",
        15 => "ServerQuery",
        26 => "ClientLoaded",
        29 => "ClientPlaying",
        33 => "LoginTokenQuery",
        34 => "RequestPositionTCP",
        35 => "UdpPacket",
        _  => "?"
    };

    private static string ServerIdName(int id) => id switch
    {
        1  => "ServerIdentification",
        9  => "DisconnectPlayer",
        29 => "ServerRedirect",
        73 => "ServerReady",
        77 => "TokenAnswer",
        78 => "RequestPositionTCP",
        79 => "UdpPacket",
        81 => "DidReceiveUdp",
        _  => "?"
    };
}
