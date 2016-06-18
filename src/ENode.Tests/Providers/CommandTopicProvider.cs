﻿using ENode.Commanding;
using ENode.EQueue;

namespace ENode.Tests
{
    public class CommandTopicProvider : AbstractTopicProvider<ICommand>
    {
        public override string GetTopic(ICommand command)
        {
            return "CommandTopic";
        }
    }
}
