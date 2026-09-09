namespace S7CommPlusDriver
{
    /// <summary>
    /// Identifies the client role used when establishing an S7CommPlus session.
    /// </summary>
    public enum S7CommPlusSessionRole
    {
        /// <summary>
        /// Uses the HMI endpoint and, for legacy challenge authentication, the HMI server-session role.
        /// </summary>
        Hmi = 0,

        /// <summary>
        /// Uses the engineering-system endpoint and, for legacy challenge authentication, the engineering server-session role.
        /// </summary>
        EngineeringSystem = 1
    }
}
