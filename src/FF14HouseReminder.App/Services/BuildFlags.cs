namespace FF14HouseReminder.Services;

/// <summary>编译期功能开关</summary>
public static class BuildFlags
{
#if PUBLIC_BUILD
    /// <summary>公开版：不包含本地直报接收服务</summary>
    public const bool HasLocalIngest = false;
#else
    public const bool HasLocalIngest = true;
#endif
}
