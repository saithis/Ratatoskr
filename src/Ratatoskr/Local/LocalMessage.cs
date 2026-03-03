using Ratatoskr.Core;

namespace Ratatoskr.Local;

internal record LocalMessage(byte[] Content, MessageProperties Properties, string ChannelName);
