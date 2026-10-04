namespace Google.Protobuf.Collections
{
    // Metadata only: these fixtures must never be instantiated by the importer.
    public class RepeatedField<T> { }
}

namespace Fixture.Protocol
{
    using Google.Protobuf.Collections;

    public class ApiRequest
    {
        public const int PostAuthSessionFieldNumber = 303, PostPvtUserTitleFieldNumber = 353,
            PostPvtGiftListFieldNumber = 376, PostPvtGiftHistoryFieldNumber = 377,
            PostPvtFriendListFieldNumber = 481, PostPvtFriendRequestListFieldNumber = 482,
            PostPvtFriendReceiveListFieldNumber = 483, PostPvtGuildWatchListFieldNumber = 569,
            PostPvtGuildMemberListFieldNumber = 570, PostPvtUserDenyListFieldNumber = 587;
        public object PostAuthSession { get; set; } = null!;
        public object PostPvtUserTitle { get; set; } = null!;
        public object PostPvtGiftList { get; set; } = null!;
        public object PostPvtGiftHistory { get; set; } = null!;
        public object PostPvtFriendList { get; set; } = null!;
        public object PostPvtFriendRequestList { get; set; } = null!;
        public object PostPvtFriendReceiveList { get; set; } = null!;
        public object PostPvtGuildWatchList { get; set; } = null!;
        public object PostPvtGuildMemberList { get; set; } = null!;
        public object PostPvtUserDenyList { get; set; } = null!;
    }
    public class ApiResponse : ApiRequest
    {
        public const int CommonFieldNumber = 101, PostPvtStorePurchaseRestartSteamFieldNumber = 2001;
        public CommonResponse Common { get; set; } = null!;
        public object PostPvtStorePurchaseRestartSteam { get; set; } = null!;
        // The metadata reader intentionally does not infer inherited fields.
        public new const int PostAuthSessionFieldNumber = 303, PostPvtUserTitleFieldNumber = 353,
            PostPvtGiftListFieldNumber = 376, PostPvtGiftHistoryFieldNumber = 377,
            PostPvtFriendListFieldNumber = 481, PostPvtFriendRequestListFieldNumber = 482,
            PostPvtFriendReceiveListFieldNumber = 483, PostPvtGuildWatchListFieldNumber = 569,
            PostPvtGuildMemberListFieldNumber = 570, PostPvtUserDenyListFieldNumber = 587;
        public new object PostAuthSession { get; set; } = null!;
        public new object PostPvtUserTitle { get; set; } = null!;
        public new PostPvtGiftListResponse PostPvtGiftList { get; set; } = null!;
        public new PostPvtGiftHistoryResponse PostPvtGiftHistory { get; set; } = null!;
        public new PostPvtFriendListResponse PostPvtFriendList { get; set; } = null!;
        public new PostPvtFriendRequestListResponse PostPvtFriendRequestList { get; set; } = null!;
        public new PostPvtFriendReceiveListResponse PostPvtFriendReceiveList { get; set; } = null!;
        public new PostPvtGuildWatchListResponse PostPvtGuildWatchList { get; set; } = null!;
        public new PostPvtGuildMemberListResponse PostPvtGuildMemberList { get; set; } = null!;
        public new PostPvtUserDenyListResponse PostPvtUserDenyList { get; set; } = null!;
    }
    public class CommonResponse
    {
        public const int UserFieldNumber = 1;
        public User User { get; set; } = null!;
    }
    public class User
    {
        public const int UpdateFieldNumber = 1, DeleteFieldNumber = 2, OtherInfoFieldNumber = 3;
        public Tables Update { get; set; } = null!;
        public Tables Delete { get; set; } = null!;
        public UserOtherInfo OtherInfo { get; set; } = null!;
    }
    public class Tables
    {
        public const int UserStatusListFieldNumber = 259686066, UserWeaponListFieldNumber = 231622239,
            UserPartyListFieldNumber = 312005933, UserPartyMemberListFieldNumber = 17062056,
            UserHomeBackgroundSettingListFieldNumber = 242346576, UserStoryDramaSelectionListFieldNumber = 78231314;
        public RepeatedField<UserStatus> UserStatusList { get; set; } = null!;
        public RepeatedField<UserWeapon> UserWeaponList { get; set; } = null!;
        public RepeatedField<UserParty> UserPartyList { get; set; } = null!;
        public RepeatedField<UserPartyMember> UserPartyMemberList { get; set; } = null!;
        public RepeatedField<UserHomeBackgroundSetting> UserHomeBackgroundSettingList { get; set; } = null!;
        public RepeatedField<UserStoryDramaSelection> UserStoryDramaSelectionList { get; set; } = null!;
    }
    public class UserStatus
    {
        public const int UserIdFieldNumber = 1, ExpFieldNumber = 2;
        public long UserId { get; set; }
        public long Exp { get; set; }
    }
    public enum Quality { None, Rare }
    public class UserWeapon
    {
        public const int UserIdFieldNumber = 1, WeaponIdFieldNumber = 2, NameFieldNumber = 3,
            IsLockFieldNumber = 4, QualityFieldNumber = 5, WeightFieldNumber = 6;
        public long UserId { get; set; }
        public long WeaponId { get; set; }
        public string Name { get; set; } = null!;
        public bool IsLock { get; set; }
        public Quality Quality { get; set; }
        public ulong Weight { get; set; }
    }
    public class UserParty
    {
        public const int UserIdFieldNumber = 1, PartyIdFieldNumber = 2, NameFieldNumber = 3;
        public long UserId { get; set; }
        public long PartyId { get; set; }
        public string Name { get; set; } = null!;
    }
    public class UserPartyMember
    {
        public const int UserIdFieldNumber = 1, PartyMemberIdFieldNumber = 2;
        public long UserId { get; set; }
        public long PartyMemberId { get; set; }
    }
    public class UserHomeBackgroundSetting
    {
        public const int UserIdFieldNumber = 1, BackgroundIdFieldNumber = 2;
        public long UserId { get; set; }
        public long BackgroundId { get; set; }
    }
    public class UserStoryDramaSelection
    {
        public const int UserIdFieldNumber = 1, SelectionIdFieldNumber = 2, SelectionIndexFieldNumber = 3;
        public long UserId { get; set; }
        public long SelectionId { get; set; }
        public long SelectionIndex { get; set; }
    }
    public class UserOtherInfo
    {
        public const int UserStoneListFieldNumber = 1, MissionAchievedListFieldNumber = 2,
            HasGiftFieldNumber = 3, IsCombatPowerRefreshFieldNumber = 4;
        public RepeatedField<UserStone> UserStoneList { get; set; } = null!;
        public RepeatedField<UserStone> MissionAchievedList { get; set; } = null!;
        public bool HasGift { get; set; }
        public bool IsCombatPowerRefresh { get; set; }
    }
    public class UserStone
    {
        public const int CountFieldNumber = 1;
        public long Count { get; set; }
    }
    public class RewardInfo
    {
        public const int CountFieldNumber = 3;
        public long Count { get; set; }
    }
    public class HistoryGiftInfo
    {
        public const int RewardInfoFieldNumber = 1;
        public RewardInfo RewardInfo { get; set; } = null!;
    }
    public class UserListInfo
    {
        public const int PlayerNameFieldNumber = 2;
        public string PlayerName { get; set; } = null!;
    }
    public class GuildMemberListInfo
    {
        public const int UserListInfoFieldNumber = 1;
        public UserListInfo UserListInfo { get; set; } = null!;
    }
    public class GuildInfo
    {
        public const int GuildNameFieldNumber = 2;
        public string GuildName { get; set; } = null!;
    }
    public class PostPvtGiftListRequest
    {
        public const int PageNoFieldNumber = 1;
        public long PageNo { get; set; }
    }
    public class PostPvtGiftListResponse
    {
        public const int RewardInfoListFieldNumber = 1, TotalGiftCountFieldNumber = 2;
        public RepeatedField<RewardInfo> RewardInfoList { get; set; } = null!;
        public long TotalGiftCount { get; set; }
    }
    public class PostPvtGiftHistoryResponse
    {
        public const int HistoryGiftInfoListFieldNumber = 1;
        public RepeatedField<HistoryGiftInfo> HistoryGiftInfoList { get; set; } = null!;
    }
    public class PostPvtFriendListResponse
    {
        public const int UserListInfosFieldNumber = 1;
        public RepeatedField<UserListInfo> UserListInfos { get; set; } = null!;
    }
    public class PostPvtFriendReceiveListResponse
    {
        public const int UserListInfosFieldNumber = 1;
        public RepeatedField<UserListInfo> UserListInfos { get; set; } = null!;
    }
    public class PostPvtFriendRequestListResponse
    {
        public const int UserListInfosFieldNumber = 1;
        public RepeatedField<UserListInfo> UserListInfos { get; set; } = null!;
    }
    public class PostPvtUserDenyListResponse
    {
        public const int UserListInfosFieldNumber = 1;
        public RepeatedField<UserListInfo> UserListInfos { get; set; } = null!;
    }
    public class PostPvtGuildMemberListResponse
    {
        public const int GuildMemberListInfosFieldNumber = 1;
        public RepeatedField<GuildMemberListInfo> GuildMemberListInfos { get; set; } = null!;
    }
    public class PostPvtGuildWatchListResponse
    {
        public const int GuildInfoListFieldNumber = 1;
        public RepeatedField<GuildInfo> GuildInfoList { get; set; } = null!;
    }
}
