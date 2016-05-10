﻿// ***********************************************************************
// Copyright (c) 2015 Charlie Poole
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

using System.IO;
using System.Xml;
using System.Collections.Generic;
using System.Globalization;

namespace NUnit.ConsoleRunner.Utilities
{
    using System;
    using System.Threading;

    internal class TeamCityServiceMessagePublisher
    {
        private readonly ReaderWriterLock _refsLock = new ReaderWriterLock();
        private readonly TextWriter _outWriter;
        private readonly Dictionary<string, string> _refs = new Dictionary<string, string>();
        private int _blockCounter;
        private string _rootFlowId;

        public TeamCityServiceMessagePublisher(TextWriter outWriter)
        {
            if (outWriter == null)
            {
                throw new ArgumentNullException("outWriter");
            }

            _outWriter = outWriter;
        }

        public void RegisterMessage(XmlNode message)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            var messageName = message.Name;
            if (string.IsNullOrEmpty(messageName))
            {
                return;
            }

            messageName = messageName.ToLowerInvariant();
            if (messageName == "start-run")
            {
                ClearRefs();

                return;
            }

            var fullName = message.GetAttribute("fullname");
            if (string.IsNullOrEmpty(fullName))
            {
                return;
            }

            var id = message.GetAttribute("id");
            var parentId = message.GetAttribute("parentId");
            string flowId;
            if (parentId != null)
            {
                // NUnit 3 case
                string rootId;
                flowId = TryFindRootId(parentId, out rootId) ? rootId : id;
            }
            else
            {
                // NUnit 2 case
                flowId = _rootFlowId;
            }

            string testFlowId;
            if (id != flowId && parentId != null)
            {
                testFlowId = id;
            }
            else
            {
                testFlowId = flowId;
                if (testFlowId == null)
                {
                    testFlowId = id;
                }
            }

            switch (messageName)
            {
                case "start-suite":
                    SetParent(id, parentId);
                    // NUnit 3 case
                    if (parentId == string.Empty)
                    {
                        OnRootSuiteStart(flowId, fullName);
                    }

                    // NUnit 2 case
                    if (parentId == null)
                    {
                        if (Interlocked.Increment(ref _blockCounter) == 1)
                        {
                            _rootFlowId = id;
                            OnRootSuiteStart(id, fullName);
                        }
                    }

                    break;

                case "test-suite":
                    ClearParent(id);
                    // NUnit 3 case
                    if (parentId == string.Empty)
                    {
                        OnRootSuiteFinish(flowId, fullName);
                    }

                    // NUnit 2 case
                    if (parentId == null)
                    {
                        if (Interlocked.Decrement(ref _blockCounter) == 0)
                        {
                            _rootFlowId = null;
                            OnRootSuiteFinish(id, fullName);
                        }
                    }

                    break;

                case "start-test":
                    SetParent(id, parentId);
                    if (id != flowId && parentId != null)
                    {
                        OnFlowStarted(id, flowId);
                    }
                    
                    OnTestStart(testFlowId, fullName);
                    break;

                case "test-case":
                    try
                    {
                        ClearParent(id);
                        var result = message.GetAttribute("result");
                        if (string.IsNullOrEmpty(result))
                        {
                            break;
                        }

                        switch (result.ToLowerInvariant())
                        {
                            case "passed":
                                OnTestFinished(testFlowId, message, fullName);
                                break;

                            case "inconclusive":
                                OnTestInconclusive(testFlowId, message, fullName);
                                break;

                            case "skipped":
                                OnTestSkipped(testFlowId, message, fullName);
                                break;

                            case "failed":
                                OnTestFailed(testFlowId, message, fullName);
                                break;
                        }
                    }
                    finally
                    {
                        if (id != flowId && parentId != null)
                        {
                            OnFlowFinished(id);
                        }
                    }

                    break;                
            }            
        }

        private void ClearParent(string id)
        {
            _refsLock.AcquireWriterLock(Timeout.Infinite);
            try
            {
                _refs.Remove(id);
            }
            finally
            {
                _refsLock.ReleaseWriterLock();
            }            
        }

        private void SetParent(string id, string parentId)
        {
            _refsLock.AcquireWriterLock(Timeout.Infinite);
            try
            {
                _refs[id] = parentId;
            }
            finally
            {
                _refsLock.ReleaseWriterLock();
            }
        }

        private void ClearRefs()
        {
            _refsLock.AcquireWriterLock(Timeout.Infinite);
            try
            {
                _refs.Clear();
            }
            finally
            {
                _refsLock.ReleaseWriterLock();
            }
        }

        private bool TryFindParentId(string id, out string parentId)
        {
            if (id == null)
            {
                throw new ArgumentNullException("id");
            }

            _refsLock.AcquireReaderLock(Timeout.Infinite);
            try
            {
                return _refs.TryGetValue(id, out parentId) && !string.IsNullOrEmpty(parentId);
            }
            finally
            {
                _refsLock.ReleaseReaderLock();
            }
        }

        private bool TryFindRootId(string id, out string rootId)
        {
            if (id == null)
            {
                throw new ArgumentNullException("id");
            }

            _refsLock.AcquireReaderLock(Timeout.Infinite);
            try
            {
                while (TryFindParentId(id, out rootId) && id != rootId)
                {
                    id = rootId;
                }
            }
            finally
            {
                _refsLock.ReleaseReaderLock();
            }

            rootId = id;
            return !string.IsNullOrEmpty(id);
        }

        private void TrySendOutput(string flowId, XmlNode message, string fullName)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            var output = message.SelectSingleNode("output");
            if (output == null)
            {
                return;
            }

            var outputStr = output.InnerText;
            if (string.IsNullOrEmpty(outputStr))
            {
                return;
            }

            WriteLine("##teamcity[testStdOut name='{0}' out='{1}' flowId='{2}']", fullName, outputStr, flowId);
        }

        private void OnRootSuiteStart(string flowId, string assemblyName)
        {
            assemblyName = Path.GetFileName(assemblyName);
            WriteLine("##teamcity[testSuiteStarted name='{0}' flowId='{1}']", assemblyName, flowId);         
        }

        private void OnRootSuiteFinish(string flowId, string assemblyName)
        {
            assemblyName = Path.GetFileName(assemblyName);
            WriteLine("##teamcity[testSuiteFinished name='{0}' flowId='{1}']", assemblyName, flowId);            
        }

        private void OnFlowStarted(string flowId, string parentFlowId)
        {
            WriteLine("##teamcity[flowStarted flowId='{0}' parent='{1}']", flowId, parentFlowId);            
        }

        private void OnFlowFinished(string flowId)
        {
            WriteLine("##teamcity[flowFinished flowId='{0}']", flowId);
        }

        private void OnTestStart(string flowId, string fullName)
        {
            WriteLine("##teamcity[testStarted name='{0}' captureStandardOutput='false' flowId='{1}']", fullName, flowId);
        }

        private void OnTestFinished(string flowId, XmlNode message, string fullName)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            var durationStr = message.GetAttribute("duration");
            double durationDecimal;
            int durationMilliseconds = 0;
            if (durationStr != null && double.TryParse(durationStr, NumberStyles.Any, CultureInfo.InvariantCulture, out durationDecimal))
            {
                durationMilliseconds = (int)(durationDecimal * 1000d);
            }

            TrySendOutput(flowId, message, fullName);
            WriteLine(
                "##teamcity[testFinished name='{0}' duration='{1}' flowId='{2}']",
                fullName,
                durationMilliseconds.ToString(),
                flowId);            
        }

        private void OnTestFailed(string flowId, XmlNode message, string fullName)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            var errorMmessage = message.SelectSingleNode("failure/message");
            var stackTrace = message.SelectSingleNode("failure/stack-trace");
            WriteLine(
                "##teamcity[testFailed name='{0}' message='{1}' details='{2}' flowId='{3}']",
                fullName,
                errorMmessage == null ? string.Empty : errorMmessage.InnerText,
                stackTrace == null ? string.Empty : stackTrace.InnerText,
                flowId);

            OnTestFinished(flowId, message, fullName);
        }

        private void OnTestSkipped(string flowId, XmlNode message, string fullName)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            TrySendOutput(flowId, message, fullName);
            var reason = message.SelectSingleNode("reason/message");
            WriteLine(
                "##teamcity[testIgnored name='{0}' message='{1}' flowId='{2}']",
                fullName,
                reason == null ? string.Empty : reason.InnerText,
                flowId);
        }

        private void OnTestInconclusive(string flowId, XmlNode message, string fullName)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            TrySendOutput(flowId, message, fullName);
            WriteLine(
                "##teamcity[testIgnored name='{0}' message='{1}' flowId='{2}']",
                fullName,
                "Inconclusive",
                flowId);
        }

        private void WriteLine(string format, params string[] arg)
        {
            if (format == null)
            {
                throw new ArgumentNullException("format");
            }

            if (arg == null)
            {
                throw new ArgumentNullException("arg");
            }

            var argObjects = new object[arg.Length];
            for (var i = 0; i < arg.Length; i++)
            {
                var str = arg[i];
                if (str != null)
                {
                    str = Escape(str);
                }

                argObjects[i] = str;
            }

            var message = string.Format(format, argObjects);
            _outWriter.WriteLine(message);
        }

        private static string Escape(string input)
        {
            return input != null
                ? input.Replace("|", "||")
                       .Replace("'", "|'")
                       .Replace("\n", "|n")
                       .Replace("\r", "|r")
                       .Replace(char.ConvertFromUtf32(0x0086), "|x")
                       .Replace(char.ConvertFromUtf32(0x2028), "|l")
                       .Replace(char.ConvertFromUtf32(0x2029), "|p")
                       .Replace("[", "|[")
                       .Replace("]", "|]")
                : null;
        }
    }
}
