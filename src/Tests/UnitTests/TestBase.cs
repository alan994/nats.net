﻿using System;
using System.IO;
using System.Text;
using System.Threading;

namespace UnitTests
{
    public class TestBase
    {
        public const string Plain        = "plain";
        public const string HasSpace     = "has space";
        public const string HasPrintable = "has-print!able";
        public const string HasDot       = "has.dot";
        public const string HasStar      = "has*star";
        public const string HasGt        = "has>gt";
        public const string HasDash      = "has-dash";
        public const string HasUnder     = "has_under";
        public const string HasDollar    = "has$dollar";
        public const string HasLow       = "has\tlower\rthan\nspace";
        public const string HasFwdSlash  = "has/fwd/slash";
        public const string HasEquals    = "has=equals";
        public const string HasTic       = "has`tic";
        public static readonly string Has127 = "has" + (char)127 + "127";

        public static string ReadDataFile(string name)
        {
            return File.ReadAllText(FileSpec(name));
        }

        public static string[] ReadDataFileLines(string name)
        {
            return File.ReadAllLines(FileSpec(name));
        }

        private static string FileSpec(string name)
        {
            string path = Directory.GetCurrentDirectory();
            return Path.Combine(path, "..", "..", "..", "Data", name);
        }

        public static DateTime AsDateTime(string dtString)
        {
            return DateTime.Parse(dtString).ToUniversalTime();
        }
        
        // ----------------------------------------------------------------------------------------------------
        // data makers
        // ----------------------------------------------------------------------------------------------------
        public const string STREAM = "stream";
        public const string MIRROR = "mirror";
        public const string SOURCE = "source";
        public const string SUBJECT = "subject";
        public const string SUBJECT_STAR = SUBJECT + ".*";
        public const string SUBJECT_GT = SUBJECT + ".>";
        public const string QUEUE = "queue";
        public const string DURABLE = "durable";
        public const string DELIVER = "deliver";
        public const string MESSAGE_ID = "mid";
        public const string DATA = "data";

        public static string Stream(int seq) {
            return STREAM + "-" + seq;
        }

        public static string Mirror(int seq) {
            return MIRROR + "-" + seq;
        }

        public static string Source(int seq) {
            return SOURCE + "-" + seq;
        }

        public static string Subject(int seq) {
            return SUBJECT + "-" + seq;
        }

        public static String SubjectDot(String field) {
            return SUBJECT + "." + field;
        }

        public static string Queue(int seq) {
            return QUEUE + "-" + seq;
        }

        public static string Durable(int seq) {
            return DURABLE + "-" + seq;
        }

        public static string Durable(string vary, int seq) {
            return DURABLE + "-" + vary + "-" + seq;
        }

        public static string Deliver(int seq) {
            return DELIVER + "-" + seq;
        }

        public static string MessageId(int seq) {
            return MESSAGE_ID + "-" + seq;
        }

        public static string Data(int seq) {
            return DATA + "-" + seq;
        }

        public static byte[] DataBytes() {
            return Encoding.ASCII.GetBytes(DATA);
        }

        public static byte[] DataBytes(int seq) {
            return Encoding.ASCII.GetBytes(Data(seq));
        }
    }

    public class InterlockedLong
    {
        private long count;

        public InterlockedLong() {}

        public InterlockedLong(long start)
        {
            this.count = start;
        }

        public void Set(long l)
        {
            Interlocked.Exchange(ref count, l);
        }

        public long Increment()
        {
            return Interlocked.Increment(ref count);
        }

        public long Read()
        {
            return Interlocked.Read(ref count);
        }
    }

    public class InterlockedInt
    {
        private readonly InterlockedLong il;

        public InterlockedInt()
        {
            il = new InterlockedLong();
        }

        public InterlockedInt(long start)
        {
            il = new InterlockedLong(start);
        }

        public void Set(int i)
        {
            il.Set(i);
        }

        public int Increment()
        {
            return (int)il.Increment();
        }

        public int Read()
        {
            return (int)il.Read();
        }
    }
}