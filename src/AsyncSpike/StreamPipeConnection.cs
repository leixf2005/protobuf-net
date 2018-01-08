// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace System.IO.Pipelines
{
    public class StreamPipeConnection : IPipeConnection
    {
        public StreamPipeConnection(PipeFactory factory, Stream stream)
        {
            Input = CreateReader(factory, stream);
            Output = CreateWriter(factory, stream);
        }

        public IPipeReader Input { get; }

        public IPipeWriter Output { get; }

        public void Dispose()
        {
            Input.Complete();
            Output.Complete();
        }

        public static IPipeReader CreateReader(PipeFactory factory, Stream stream)
        {
            if (!stream.CanRead)
            {
                throw new NotSupportedException();
            }

            var pipe = factory.Create();
            var ignore = stream.CopyToEndAsync(pipe.Writer);

            return pipe.Reader;
        }

        public static IPipeWriter CreateWriter(PipeFactory factory, Stream stream)
        {
            var pipe = factory.Create();
            if (stream.CanWrite)
            {
                var _ = pipe.Reader.CopyToEndAsync(stream);
            }
            else
            {
                pipe.Writer.Complete();
            }
            return pipe.Writer;
        }
    }
}