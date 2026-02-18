using System.ComponentModel.DataAnnotations;

namespace Yap.Models;

/// <summary>
/// User actions log, such as login etc
/// </summary>
public class UserActionLog
{
    public int Id { get; set; }

    public DateTime Date { get; set; }

    public string UserUid { get; set; }

    /// <summary>
    /// What type of action
    /// </summary>
    [MaxLength(128)]
    public string Action { get; set; }

    /// <summary>
    /// optional url
    /// </summary>
    public string Url { get; set; }

    /// <summary>
    /// optional additional info
    /// </summary>
    public string Info { get; set; }

    /// <summary>
    /// IP address
    /// </summary>
    public string IP { get; set; }


    public static class KnownActions
    {
        /// <summary>
        /// default, but should be avoided if possible. Use more specific action types when possible.
        /// </summary>
        public const string UNKNOWN = "UNKNOWN";

        public const string LOGIN = "LOGIN";

        /// <summary>
        /// sign out, not just disconnect. This should be used when the user explicitly logs out, but not for session expiration or closing the browser.
        /// </summary>
        public const string LOGOUT = "LOGOUT";

        public const string CIRCUIT_RECONNECT = "CIRCUIT_RECONNECT";

        public const string CIRCUIT_DISCONNECT = "CIRCUIT_DISCONNECT";

        /// <summary>
        /// each http request
        /// </summary>
        public const string HTTP_REQUEST = "HTTP_REQUEST";

    }

}
