namespace databox_status
{
    public static class Utils
    {
        public static string GetCosmosIdFromJobId(string jobId)
        {
            var result = jobId.Replace('/', '-');

            return result.Substring(1, (result.Length-1));
        }
    }
}
