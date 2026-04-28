using System.IO;

namespace CupheadOnline.Net
{
    public interface IPacket
    {
        void Write(BinaryWriter w);
        void Read(BinaryReader r);
    }

    public enum PacketType : byte
    {
        PlayerState = 0,
        InputFrame = 1,
        WeaponEvent = 2,
        DamageEvent = 3,
        EnemyState = 4,
        SceneChange = 5,
        LobbySync = 6,
        Ping = 7,
        Pong = 8,
        SessionStart = 9,
        Disconnect = 10,
        Hello = 11,
        Welcome = 12,
        Ready = 13,
        MenuSceneChange = 14,
        SaveSlotSync = 15,
        SaveProfile = 16,
        SessionSnapshot = 17,
        SessionSignal = 18,
        PlayerStatus = 19,
        ReviveRequest = 20,
        ReviveGrant = 21,
        MapDialogue = 22,
        BattleAssistStats = 23,
        ReviveVisual = 24,
    }

    public enum SessionSignalKind : byte
    {
        None = 0,
        GuestReady = 1,
        GuestUnready = 2,
        RequestRecovery = 3,
        LanSteamE2ECheckpoint = 4,
        LevelLoaded = 5,
        LevelStartRelease = 6,
        MapDialogueStartedObserved = 7,
        MapDialogueContinueObserved = 8,
        LanSteamE2EPauseObserved = 9,
        LanSteamE2EUnpauseObserved = 10,
        LanSteamE2EReviveTestStarted = 11,
        LanSteamE2EReviveObserved = 12,
        LanSteamE2EReverseReviveTestStarted = 13,
        LanSteamE2EReverseReviveObserved = 14,
    }

    public enum MapDialogueAction : byte
    {
        Start = 1,
        Continue = 2,
        End = 3,
    }

    public struct PlayerStatePacket : IPacket
    {
        public const byte MapStateFlag = 128;

        public byte PlayerId;
        public float PosX;
        public float PosY;
        public sbyte LookX;
        public sbyte LookY;
        public byte Flags;
        public byte AnimState;
        public uint Tick;
        public int AnimHash;
        public float AnimNormalizedTime;

        public bool Grounded => (Flags & 1) != 0;
        public bool Dashing => (Flags & 2) != 0;
        public bool Ducking => (Flags & 4) != 0;
        public bool GravReversed => (Flags & 8) != 0;
        public bool IsHit => (Flags & 16) != 0;
        public bool IsSuper => (Flags & 32) != 0;
        public bool IsDead => (Flags & 64) != 0;
        public bool IsMapState => (Flags & MapStateFlag) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(PlayerId);
            w.Write(PosX);
            w.Write(PosY);
            w.Write(LookX);
            w.Write(LookY);
            w.Write(Flags);
            w.Write(AnimState);
            w.Write(Tick);
            w.Write(AnimHash);
            w.Write(AnimNormalizedTime);
        }

        public void Read(BinaryReader r)
        {
            PlayerId = r.ReadByte();
            PosX = r.ReadSingle();
            PosY = r.ReadSingle();
            LookX = r.ReadSByte();
            LookY = r.ReadSByte();
            Flags = r.ReadByte();
            AnimState = r.ReadByte();
            Tick = r.ReadUInt32();
            AnimHash = AnimState;
            AnimNormalizedTime = 0f;
            if (r.BaseStream.Position <= r.BaseStream.Length - 4)
                AnimHash = r.ReadInt32();
            if (r.BaseStream.Position <= r.BaseStream.Length - 4)
                AnimNormalizedTime = r.ReadSingle();
        }
    }

    public struct InputFramePacket : IPacket
    {
        public float AxisX;
        public float AxisY;
        public uint Buttons;
        public uint Tick;

        public bool IsPressed(CupheadButton btn) => (Buttons & (1u << (int)btn)) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(AxisX);
            w.Write(AxisY);
            w.Write(Buttons);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            AxisX = r.ReadSingle();
            AxisY = r.ReadSingle();
            Buttons = r.ReadUInt32();
            Tick = r.ReadUInt32();
        }
    }

    public struct WeaponEventPacket : IPacket
    {
        public byte PlayerId;
        public byte EventType;
        public sbyte AimX;
        public sbyte AimY;
        public byte WeaponId;
        public uint Tick;

        public void Write(BinaryWriter w)
        {
            w.Write(PlayerId);
            w.Write(EventType);
            w.Write(AimX);
            w.Write(AimY);
            w.Write(WeaponId);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            PlayerId = r.ReadByte();
            EventType = r.ReadByte();
            AimX = r.ReadSByte();
            AimY = r.ReadSByte();
            WeaponId = r.ReadByte();
            Tick = r.ReadUInt32();
        }
    }

    public struct DamageEventPacket : IPacket
    {
        public byte TargetPlayerId;
        public float Damage;
        public float StoneTime;
        public byte Source;
        public uint Tick;

        public void Write(BinaryWriter w)
        {
            w.Write(TargetPlayerId);
            w.Write(Damage);
            w.Write(StoneTime);
            w.Write(Source);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            TargetPlayerId = r.ReadByte();
            Damage = r.ReadSingle();
            StoneTime = r.ReadSingle();
            Source = r.ReadByte();
            Tick = r.ReadUInt32();
        }
    }

    public struct EnemyStatePacket : IPacket
    {
        public int InstanceId;
        public float PosX;
        public float PosY;
        public float Hp;
        public byte Phase;
        public int AnimHash;
        public uint Tick;
        public float BossHp;
        public float BossTotalHp;
        public float AnimNormalizedTime;

        public void Write(BinaryWriter w)
        {
            w.Write(InstanceId);
            w.Write(PosX);
            w.Write(PosY);
            w.Write(Hp);
            w.Write(Phase);
            w.Write(AnimHash);
            w.Write(Tick);
            w.Write(BossHp);
            w.Write(BossTotalHp);
            w.Write(AnimNormalizedTime);
        }

        public void Read(BinaryReader r)
        {
            InstanceId = r.ReadInt32();
            PosX = r.ReadSingle();
            PosY = r.ReadSingle();
            Hp = r.ReadSingle();
            Phase = r.ReadByte();
            AnimHash = r.ReadInt32();
            Tick = r.ReadUInt32();
            BossHp = -1f;
            BossTotalHp = -1f;
            AnimNormalizedTime = 0f;
            if (r.BaseStream.Position <= r.BaseStream.Length - 8)
            {
                BossHp = r.ReadSingle();
                BossTotalHp = r.ReadSingle();
            }
            if (r.BaseStream.Position <= r.BaseStream.Length - 4)
                AnimNormalizedTime = r.ReadSingle();
        }
    }

    public struct SceneChangePacket : IPacket
    {
        public int LevelEnum;
        public uint RngSeed;

        public void Write(BinaryWriter w)
        {
            w.Write(LevelEnum);
            w.Write(RngSeed);
        }

        public void Read(BinaryReader r)
        {
            LevelEnum = r.ReadInt32();
            RngSeed = r.ReadUInt32();
        }
    }

    public struct LobbySyncPacket : IPacket
    {
        public byte PlayerId;
        public int Weapon1;
        public int Weapon2;
        public int Super;
        public int Charm;
        public byte IsChalice;

        public void Write(BinaryWriter w)
        {
            w.Write(PlayerId);
            w.Write(Weapon1);
            w.Write(Weapon2);
            w.Write(Super);
            w.Write(Charm);
            w.Write(IsChalice);
        }

        public void Read(BinaryReader r)
        {
            PlayerId = r.ReadByte();
            Weapon1 = r.ReadInt32();
            Weapon2 = r.ReadInt32();
            Super = r.ReadInt32();
            Charm = r.ReadInt32();
            IsChalice = r.ReadByte();
        }
    }

    public struct SessionStartPacket : IPacket
    {
        public byte Flags;
        public int CurrentLevel;
        public ushort SaveRevision;
        public uint CurrentTick;
        public uint RngSeed;

        public bool IsInLevel => (Flags & 1) != 0;
        public bool HasTrackedSave => (Flags & 2) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(Flags);
            w.Write(CurrentLevel);
            w.Write(SaveRevision);
            w.Write(CurrentTick);
            w.Write(RngSeed);
        }

        public void Read(BinaryReader r)
        {
            Flags = r.ReadByte();
            CurrentLevel = r.ReadInt32();
            SaveRevision = r.ReadUInt16();
            CurrentTick = r.ReadUInt32();
            RngSeed = r.ReadUInt32();
        }
    }

    public struct MenuSceneChangePacket : IPacket
    {
        public int SceneEnum;
        public byte TransitionStart;
        public byte TransitionEnd;
        public byte Icon;
        public uint RngSeed;

        public void Write(BinaryWriter w)
        {
            w.Write(SceneEnum);
            w.Write(TransitionStart);
            w.Write(TransitionEnd);
            w.Write(Icon);
            w.Write(RngSeed);
        }

        public void Read(BinaryReader r)
        {
            SceneEnum = r.ReadInt32();
            TransitionStart = r.ReadByte();
            TransitionEnd = r.ReadByte();
            Icon = r.ReadByte();
            RngSeed = r.ReadUInt32();
        }
    }

    public struct SaveSlotSyncPacket : IPacket
    {
        public byte SlotIndex;
        public byte Flags;
        public ushort SaveRevision;
        public int CurrentMapScene;

        public bool IsEmpty => (Flags & 1) != 0;
        public bool Player1IsMugman => (Flags & 2) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(SlotIndex);
            w.Write(Flags);
            w.Write(SaveRevision);
            w.Write(CurrentMapScene);
        }

        public void Read(BinaryReader r)
        {
            SlotIndex = r.ReadByte();
            Flags = r.ReadByte();
            SaveRevision = r.ReadUInt16();
            CurrentMapScene = r.ReadInt32();
        }
    }

    public struct SaveProfilePacket : IPacket
    {
        public byte SlotIndex;
        public byte Flags;
        public int CurrentMapScene;
        public float CompletionPct;
        public float CompletionPctDlc;
        public ushort Coins;
        public int Weapon1;
        public int Weapon2;
        public int Super;
        public int Charm;

        public bool IsEmpty => (Flags & 1) != 0;
        public bool DlcEnabled => (Flags & 2) != 0;
        public bool Player1IsMugman => (Flags & 4) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(SlotIndex);
            w.Write(Flags);
            w.Write(CurrentMapScene);
            w.Write(CompletionPct);
            w.Write(CompletionPctDlc);
            w.Write(Coins);
            w.Write(Weapon1);
            w.Write(Weapon2);
            w.Write(Super);
            w.Write(Charm);
        }

        public void Read(BinaryReader r)
        {
            SlotIndex = r.ReadByte();
            Flags = r.ReadByte();
            CurrentMapScene = r.ReadInt32();
            CompletionPct = r.ReadSingle();
            CompletionPctDlc = r.ReadSingle();
            Coins = r.ReadUInt16();
            Weapon1 = r.ReadInt32();
            Weapon2 = r.ReadInt32();
            Super = r.ReadInt32();
            Charm = r.ReadInt32();
        }
    }

    public struct SessionSnapshotPacket : IPacket
    {
        public byte SaveSlotIndex;
        public byte Flags;
        public ushort SaveRevision;
        public int CurrentLevel;
        public int CurrentMapScene;
        public uint HostTick;
        public float BattleElapsedSeconds;
        public ushort BattleDeaths;
        public ushort BattleRetries;
        public ushort BattleParries;
        public string SceneName;

        public bool HasTrackedSave => (Flags & 1) != 0;
        public bool IsInLevel => (Flags & 2) != 0;
        public bool IsPaused => (Flags & 4) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(SaveSlotIndex);
            w.Write(Flags);
            w.Write(SaveRevision);
            w.Write(CurrentLevel);
            w.Write(CurrentMapScene);
            w.Write(HostTick);
            w.Write(BattleElapsedSeconds);
            w.Write(BattleDeaths);
            w.Write(BattleRetries);
            w.Write(BattleParries);
            w.Write(SceneName ?? string.Empty);
        }

        public void Read(BinaryReader r)
        {
            SaveSlotIndex = r.ReadByte();
            Flags = r.ReadByte();
            SaveRevision = r.ReadUInt16();
            CurrentLevel = r.ReadInt32();
            CurrentMapScene = r.ReadInt32();
            HostTick = r.ReadUInt32();
            BattleElapsedSeconds = 0f;
            BattleDeaths = 0;
            BattleRetries = 0;
            BattleParries = 0;
            if (r.BaseStream.Position <= r.BaseStream.Length - 10)
            {
                BattleElapsedSeconds = r.ReadSingle();
                BattleDeaths = r.ReadUInt16();
                BattleRetries = r.ReadUInt16();
                BattleParries = r.ReadUInt16();
            }
            SceneName = r.ReadString();
        }
    }

    public struct BattleAssistStatsPacket : IPacket
    {
        public byte ParticipantId;
        public int CurrentLevel;
        public ushort Deaths;
        public ushort Parries;
        public uint Tick;

        public void Write(BinaryWriter w)
        {
            w.Write(ParticipantId);
            w.Write(CurrentLevel);
            w.Write(Deaths);
            w.Write(Parries);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            ParticipantId = r.ReadByte();
            CurrentLevel = r.ReadInt32();
            Deaths = r.ReadUInt16();
            Parries = r.ReadUInt16();
            Tick = r.ReadUInt32();
        }
    }

    public struct SessionSignalPacket : IPacket
    {
        public byte Signal;
        public ushort SaveRevision;
        public float HostBattleElapsed;
        public uint Tick;

        public SessionSignalKind Kind => (SessionSignalKind)Signal;

        public void Write(BinaryWriter w)
        {
            w.Write(Signal);
            w.Write(SaveRevision);
            w.Write(HostBattleElapsed);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            Signal = r.ReadByte();
            SaveRevision = r.ReadUInt16();
            HostBattleElapsed = -1f;
            Tick = 0u;
            if (r.BaseStream.Position <= r.BaseStream.Length - 4)
                HostBattleElapsed = r.ReadSingle();
            if (r.BaseStream.Position <= r.BaseStream.Length - 4)
                Tick = r.ReadUInt32();
        }
    }

    public struct PlayerStatusPacket : IPacket
    {
        public byte ParticipantId;
        public byte Health;
        public byte HealthMax;
        public byte Flags;
        public uint Tick;

        public bool IsDead => (Flags & 1) != 0;
        public bool CanDonate => (Flags & 2) != 0;
        public bool IsChalice => (Flags & 4) != 0;
        public bool IsMugman => (Flags & 8) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(ParticipantId);
            w.Write(Health);
            w.Write(HealthMax);
            w.Write(Flags);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            ParticipantId = r.ReadByte();
            Health = r.ReadByte();
            HealthMax = r.ReadByte();
            Flags = r.ReadByte();
            Tick = r.ReadUInt32();
        }
    }

    public struct ReviveRequestPacket : IPacket
    {
        public float PosX;
        public float PosY;
        public uint Tick;

        public void Write(BinaryWriter w)
        {
            w.Write(PosX);
            w.Write(PosY);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            PosX = r.ReadSingle();
            PosY = r.ReadSingle();
            Tick = r.ReadUInt32();
        }
    }

    public struct ReviveGrantPacket : IPacket
    {
        public byte TargetParticipantId;
        public byte DonorParticipantId;
        public byte Flags;
        public float RevivePosX;
        public float RevivePosY;
        public uint Tick;

        public bool ApplyDonorCost => (Flags & 1) != 0;
        public bool ApplyRevive => (Flags & 2) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(TargetParticipantId);
            w.Write(DonorParticipantId);
            w.Write(Flags);
            w.Write(RevivePosX);
            w.Write(RevivePosY);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            TargetParticipantId = r.ReadByte();
            DonorParticipantId = r.ReadByte();
            Flags = r.ReadByte();
            RevivePosX = r.ReadSingle();
            RevivePosY = r.ReadSingle();
            Tick = r.ReadUInt32();
        }
    }

    public struct MapDialoguePacket : IPacket
    {
        public byte Action;
        public int DialogueId;
        public int Choice;
        public uint Tick;

        public MapDialogueAction Kind => (MapDialogueAction)Action;

        public void Write(BinaryWriter w)
        {
            w.Write(Action);
            w.Write(DialogueId);
            w.Write(Choice);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            Action = r.ReadByte();
            DialogueId = r.ReadInt32();
            Choice = r.ReadInt32();
            Tick = r.ReadUInt32();
        }
    }

    public struct ReviveVisualPacket : IPacket
    {
        public byte TargetParticipantId;
        public byte DonorParticipantId;
        public byte Flags;
        public float PosX;
        public float PosY;
        public uint Tick;
        public float HostBattleElapsed;

        public bool ParrySwitch => (Flags & 1) != 0;
        public bool HasHostBattleElapsed => HostBattleElapsed >= 0f;

        public void Write(BinaryWriter w)
        {
            w.Write(TargetParticipantId);
            w.Write(DonorParticipantId);
            w.Write(Flags);
            w.Write(PosX);
            w.Write(PosY);
            w.Write(Tick);
            w.Write(HostBattleElapsed);
        }

        public void Read(BinaryReader r)
        {
            TargetParticipantId = r.ReadByte();
            DonorParticipantId = r.ReadByte();
            Flags = r.ReadByte();
            PosX = r.ReadSingle();
            PosY = r.ReadSingle();
            Tick = r.ReadUInt32();
            HostBattleElapsed = -1f;
            if (r.BaseStream.Position <= r.BaseStream.Length - 4)
                HostBattleElapsed = r.ReadSingle();
        }
    }
}
