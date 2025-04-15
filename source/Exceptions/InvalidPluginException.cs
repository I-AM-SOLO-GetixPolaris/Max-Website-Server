using System;

namespace MWServer
{
    /// 当插件验证失败时抛出的自定义异常
    public class InvalidPluginException : Exception
    {
        /// 初始化带有错误消息的新实例
        public InvalidPluginException(string message) : base(message)
        {
        }
    }
}