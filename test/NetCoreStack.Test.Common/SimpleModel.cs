using System;

namespace NetCoreStack.Test.Common
{
    public class SimpleModel
    {
        public string Name { get; set; }
        public DateTime Date { get; set; }
        public object Value { get; set; }
    }

    public class Post
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
    }
}
