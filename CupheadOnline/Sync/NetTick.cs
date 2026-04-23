namespace CupheadOnline.Sync
{
    internal static class NetTick
    {
        internal static bool IsNewer(uint candidate, uint baseline)
        {
            return unchecked((int)(candidate - baseline)) > 0;
        }

        internal static bool IsOlder(uint candidate, uint baseline)
        {
            return unchecked((int)(candidate - baseline)) < 0;
        }
    }
}
