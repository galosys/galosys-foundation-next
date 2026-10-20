namespace Galosys.Foundation.Doris
{
    public class DorisOptions
    {
        public string FeHost { get; set; } = "127.0.0.1";
        public int HttpPort { get; set; } = 8030;   // FE http_port，不是9030
        public string User { get; set; } = "root";
        public string Password { get; set; } = "";
    }
}