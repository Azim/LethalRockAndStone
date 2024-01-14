using BepInEx;
using BepInEx.Logging;
using Dissonance;
using GameNetcodeStuff;
using LethalCompanyInputUtils.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Xml.Linq;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using static UnityEngine.UIElements.StylePropertyAnimationSystem;

namespace LethalRockAndStone
{

    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class LethalRockAndStonePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource mls;
        internal static RockAndStoneBinds keys = new();

        internal static PlayerControllerB player => GameNetworkManager.Instance.localPlayerController;
        internal static bool IsHost => NetworkManager.Singleton.IsHost;
        internal static bool IsClient => NetworkManager.Singleton.IsClient;
        internal static CustomMessagingManager MessageManager => NetworkManager.Singleton.CustomMessagingManager;

        internal static float VoiceVolume = 0.7f;


        internal static readonly System.Random random = new();

        internal readonly List<string> dwarfs = new()
        {
            "driller",
            "engineer",
            "gunner",
            //"original",
            "scout"
        };
        internal string currentDwarf = "driller";

        public static Dictionary<string, List<AudioClip>> sounds = new();
        public static Dictionary<ulong, long> lastBroadcastEnded = new();

        internal static T GetPrivateField<T>(object instance, string fieldName)
        {
            const BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo field = instance.GetType().GetField(fieldName, bindFlags);
            return (T)field.GetValue(instance);
        }

        private void Awake()
        {
            // Plugin startup logic
            mls = BepInEx.Logging.Logger.CreateLogSource(PluginInfo.PLUGIN_GUID);
            mls.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            loadAudioClips();
            VoiceVolume = Config.Bind<float>("General", "Volume", 0.7f, "Voiceline volume multiplier").Value;

            keys.RockAndStoneKey.performed += RockAndStoneKeyPerformed;

            On.GameNetcodeStuff.PlayerControllerB.ConnectClientToPlayerObject += playerConnected;

            On.GameNetworkManager.StartDisconnect += (orig, self) =>
            {
                orig(self);
                MessageManager.UnregisterNamedMessageHandler(PluginInfo.PLUGIN_GUID + "_OnRequestBroadcast");
                MessageManager.UnregisterNamedMessageHandler(PluginInfo.PLUGIN_GUID + "_OnReceiveBroadcast");
            };
        }

        public void loadAudioClips()
        {
            foreach (string dwarf in dwarfs)
            {
                sounds.Add(dwarf, new List<AudioClip>());
            }
            foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                mls.LogInfo("Found resource called " + resource);
            }

            AssetBundle ass = AssetBundle.LoadFromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream(Assembly.GetExecutingAssembly().GetManifestResourceNames()[0]));

            foreach (string assname in ass.GetAllAssetNames())
            {
                mls.LogInfo("Found asset " + assname);
                //mls.LogInfo("Substring: " + assname.Substring(30));
                string name = assname.Substring(30);
                int index = name.IndexOf("/");
                if (index < 0) continue;

                name = name.Substring(0, index);
                mls.LogInfo("Dwarf: " + name);
                if (!dwarfs.Contains(name)) continue;

                sounds[name].Add(ass.LoadAsset<AudioClip>(assname));
            }

        }

        public bool canSendBroadcast(ulong id, long duration)
        {
            long now = new DateTimeOffset(DateTime.Now).ToUnixTimeMilliseconds();
            mls.LogInfo($"now: {now}, last: {lastBroadcastEnded.GetValueOrDefault(id, 0)}");
            if ((now - lastBroadcastEnded.GetValueOrDefault(id, 0)) >= 0)
            {
                lastBroadcastEnded[id] = now + duration;
                //lastBroadcastEnded.Add(id, now);
                mls.LogInfo("returned true");
                return true;
            }

            return false;
        }

        public void RequestBroadcast(int id)
        {
            mls.LogInfo("RequestBroadcast");
            byte[] array = SerializeToBytes(currentDwarf, id, player.playerSteamId);
            int len = array.Length;

            mls.LogInfo($"IsHost: {IsHost}|IsClient: {IsClient}");

            if (IsHost)
            {
                mls.LogInfo("RequestBroadcast - IsHost");
                
                broadcast(NetworkManager.Singleton.LocalClientId, len, array);
                return;
            }

            mls.LogInfo("RequestBroadcast - !IsHost");

            using FastBufferWriter stream = new(len + sizeof(int), Allocator.Temp);
            try
            {
                stream.WriteValueSafe(in len, default);
                stream.WriteBytesSafe(array);

                MessageManager.SendNamedMessage(PluginInfo.PLUGIN_GUID + "_OnRequestBroadcast", 0uL, stream);
            }
            catch (Exception e)
            {
                mls.LogInfo($"Error occurred requesting broadcast\n{e}");
            }
        }

        public void OnRequestBroadcast(ulong senderId, FastBufferReader reader)
        {
            if (!IsHost) return;

            mls.LogInfo($"Broadcast request received from client: {senderId}");

            if (!reader.TryBeginRead(sizeof(int)))
            {
                mls.LogError("client error: Could not begin reading buffer.");
                return;
            }

            reader.ReadValueSafe(out int len, default);

            if (!reader.TryBeginRead(len))
            {
                mls.LogError("client error: Cant begin read");
                return;
            }

            byte[] data = new byte[len];
            reader.ReadBytesSafe(ref data, len);

            broadcast(senderId, len, data);
        }

        public void broadcast(ulong senderId, int len, byte[] data)
        {
            mls.LogInfo("broadcast");

            CustomMessage msg = ParsePacket(data);

            if (!canSendBroadcast(senderId, (long)sounds[msg.name][msg.clipIndex].length * 1000)) return; //prevent spam


            mls.LogInfo("broadcast - canSendBroadcast");

            //ask other clients to play sound
            foreach (var client in NetworkManager.Singleton.ConnectedClientsIds)
            {
                //if (client == senderId) continue;

                using FastBufferWriter writer = new(len + sizeof(int), Allocator.Temp);
                try
                {
                    writer.WriteValueSafe(in len, default);
                    writer.WriteBytesSafe(data);

                    MessageManager.SendNamedMessage(PluginInfo.PLUGIN_GUID + "_OnReceiveBroadcast", client, writer);
                }
                catch (Exception e)
                {
                    mls.LogInfo($"Error occurred sending broadcast to client {client}\n{e}");
                }
            }

            //play locally
            playPlayerSound(msg.name, msg.clipIndex, msg.steamId);

        }

        public void OnReceiveMessage(ulong _, FastBufferReader reader)
        {
            if (!reader.TryBeginRead(sizeof(int)))
            {
                mls.LogError("client error: Could not begin reading buffer.");
                return;
            }

            reader.ReadValueSafe(out int len, default);

            if (!reader.TryBeginRead(len))
            {
                mls.LogError("client error: Cant begin read");
                return;
            }

            byte[] data = new byte[len];
            reader.ReadBytesSafe(ref data, len);

            CustomMessage msg = ParsePacket(data);

            playPlayerSound(msg.name, msg.clipIndex, msg.steamId);

            mls.LogInfo("Handled message");
        }

        [Serializable]
        public class CustomMessage
        {
            public string name;
            public int clipIndex;
            public ulong steamId;
        }

        public byte[] SerializeToBytes(string name, int index, ulong steamId)
        {
            CustomMessage toSerialise = new()
            {
                name = name,
                clipIndex = index,
                steamId = steamId
            };

            BinaryFormatter bf = new();
            using MemoryStream ms = new();

            bf.Serialize(ms, toSerialise);

            return ms.ToArray();
        }
        public CustomMessage ParsePacket(byte[] data)
        {
            using var memStream = new MemoryStream();
            var binForm = new BinaryFormatter();
            memStream.Write(data, 0, data.Length);
            memStream.Seek(0, SeekOrigin.Begin);
            CustomMessage msg = (CustomMessage)binForm.Deserialize(memStream);

            mls.LogInfo("parsed rock and stone");

            return msg;
        }

        public void playPlayerSound(string name, int clipIndex, ulong steamIdOfSender)
        {
            AudioClip clip = sounds[name][clipIndex];
            if (clip == null)
            {
                mls.LogError("ERROR PLAYING SOUND - AUDIO CLIP NOT FOUND");
                return;
            }

            //play at person
            foreach(PlayerControllerB player in StartOfRound.Instance.allPlayerScripts)
            {
                if (player.playerSteamId != steamIdOfSender) continue;

                player.itemAudio.PlayOneShot(clip, VoiceVolume);

                WalkieTalkie.TransmitOneShotAudio(player.itemAudio, clip, VoiceVolume);
            }
            //TODO walkie talkie

        }
        public void playerConnected(On.GameNetcodeStuff.PlayerControllerB.orig_ConnectClientToPlayerObject orig, PlayerControllerB self)
        {
            orig(self);


            if (IsHost)
            {
                MessageManager.RegisterNamedMessageHandler(PluginInfo.PLUGIN_GUID + "_OnRequestBroadcast", OnRequestBroadcast);

                currentDwarf = dwarfs[random.Next(dwarfs.Count)];
                mls.LogInfo("Chose dwarf " + currentDwarf);

                return;
            }

            currentDwarf = dwarfs[random.Next(dwarfs.Count)];
            mls.LogInfo("Chose dwarf " + currentDwarf);

            MessageManager.RegisterNamedMessageHandler(PluginInfo.PLUGIN_GUID + "_OnReceiveBroadcast", OnReceiveMessage);
            
        }

        public void RockAndStoneKeyPerformed(InputAction.CallbackContext obj)
        {
            if (!obj.performed) return;
            mls.LogInfo("onKey");
            if (player.isTypingChat || player.inTerminalMenu || player.isPlayerDead) return;

            RequestBroadcast(GetRandomClipIndex());
        }

        public int GetRandomClipIndex()
        {
            List<AudioClip> dwarfSounds = sounds[currentDwarf];
            return random.Next(dwarfSounds.Count);
        }
    }



    public class RockAndStoneBinds : LcInputActions
    {
        [InputAction("<Keyboard>/v", Name = "RockAndStone")]
        public InputAction RockAndStoneKey { get; set; }
    }
}