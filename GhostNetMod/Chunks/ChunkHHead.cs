﻿using FMOD.Studio;
using Microsoft.Xna.Framework;
using Monocle;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace Celeste.Mod.Ghost.Net {
    [Chunk(ChunkID)]
    /// <summary>
    /// Not sent by client, but attached to all frames by client.
    /// </summary>
    public class ChunkHHead : IChunk {

        public const string ChunkID = "nH";

        public bool IsValid => true;
        public bool IsWriteable => true;

        public uint PlayerID;

        public void Read(BinaryReader reader) {
            PlayerID = reader.ReadUInt32();
        }

        public void Write(BinaryWriter writer) {
            writer.Write(PlayerID);
        }

    }
}
