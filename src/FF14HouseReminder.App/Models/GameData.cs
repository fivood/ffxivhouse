namespace FF14HouseReminder.Models;

/// <summary>
/// 静态游戏数据：服务器列表、房区名、尺寸表。
/// 数据来源：house.ffxiv.cyou 前端内置数据。
/// </summary>
public static class GameData
{
    public record ServerInfo(int Id, string Name);
    public record DcInfo(string Name, ServerInfo[] Servers);

    public static readonly DcInfo[] DataCenters =
    [
        new("陆行鸟", [
            new(1167, "红玉海"), new(1081, "神意之地"), new(1042, "拉诺西亚"), new(1044, "幻影群岛"),
            new(1060, "萌芽池"), new(1173, "宇宙和音"), new(1174, "沃仙曦染"), new(1175, "晨曦王座")
        ]),
        new("莫古力", [
            new(1172, "白银乡"), new(1076, "白金幻象"), new(1171, "神拳痕"), new(1170, "潮风亭"),
            new(1113, "旅人栈桥"), new(1121, "拂晓之间"), new(1166, "龙巢神殿"), new(1176, "梦羽宝境")
        ]),
        new("猫小胖", [
            new(1043, "紫水栈桥"), new(1169, "延夏"), new(1106, "静语庄园"), new(1045, "摩杜纳"),
            new(1177, "海猫茶屋"), new(1178, "柔风海湾"), new(1179, "琥珀原")
        ]),
        new("豆豆柴", [
            new(1192, "水晶塔"), new(1183, "银泪湖"), new(1180, "太阳海岸"), new(1186, "伊修加德"), new(1201, "红茶川")
        ])
    ];

    public static readonly ServerInfo[] AllServers =
        DataCenters.SelectMany(dc => dc.Servers).ToArray();

    public static string GetServerName(int id) =>
        AllServers.FirstOrDefault(s => s.Id == id)?.Name ?? id.ToString();

    public static readonly string[] AreaNames = ["海雾村", "薰衣草苗圃", "高脚孤丘", "白银乡", "穹顶皓天"];

    public static string GetAreaName(int area) =>
        area >= 0 && area < AreaNames.Length ? AreaNames[area] : "未知区域";

    public static string GetSizeName(int size) => size switch
    {
        0 => "S",
        1 => "M",
        2 => "L",
        _ => "?"
    };

    /// <summary>房屋尺寸：0=S 1=M 2=L，越界 -1。地块半边长推出，见 <see cref="HousingMap"/></summary>
    public static int GetSize(int area, int houseId) => HousingMap.SizeIndex(area, houseId);
}
