using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Akka.Serialization
{
    public static class HyperionByteHelper
    {
        private static readonly Encoding encoding = Encoding.UTF8;
        private const string typeNamePattern = "\0System";
        private static readonly byte[] typeNameBytesPattern = encoding.GetBytes(typeNamePattern);

        private const string coreAssemblyName = "System.Private.CoreLib";
        private static readonly byte[] coreAssemblyBytes = encoding.GetBytes(coreAssemblyName);

        private const string fullFwkAssemblyName = "mscorlib";
        private static readonly byte[] fullFwkAssemblyBytes = encoding.GetBytes(fullFwkAssemblyName);

        private const string coreExpandoTypeNameRegexPattern = @"System.Dynamic.ExpandoObject, System.Linq.Expressions, Version=[0-9]+[.][0-9]+[.][0-9]+[.][0-9]+, PublicKeyToken=[a-zA-Z0-9]*";
        private const string coreExpandoTypeNamePattern = "System.Dynamic.ExpandoObject, System.Linq.Expressions";
        private static readonly byte[] coreExpandoTypeBytesPattern = encoding.GetBytes(coreExpandoTypeNamePattern);

        private static readonly string fullFwkExpandoTypeName = typeof(ExpandoObject).AssemblyQualifiedName;//= "System.Dynamic.ExpandoObject, System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
        private static readonly byte[] fullFwkExpandoTypeBytes = encoding.GetBytes(fullFwkExpandoTypeName);

        private static string CoreExpandoTypeName { get; set; }

        private static byte[] CoreExpandoTypeBytes { get; set; }

        private static bool IsCoreAssembly { get; }

        private static byte[] OldAssemblyBytes { get; }

        private static byte[] NewAssemblyBytes { get; }


        private static Regex expandoRegex = new Regex(coreExpandoTypeNameRegexPattern);

        static HyperionByteHelper()
        {
            //Set framework namespace
            IsCoreAssembly = CheckIsCoreFramework();
            OldAssemblyBytes = IsCoreAssembly ? fullFwkAssemblyBytes : coreAssemblyBytes;
            NewAssemblyBytes = IsCoreAssembly ? coreAssemblyBytes : fullFwkAssemblyBytes;
        }

        /// <summary>
        /// Incoming byte array message will be of the format <TypeNameLength>\0\0\0<TypeName>.
        /// <TypeName> contains the full qualified name along with the assembly which can be mscorlib or System.Private.CoreLib
        /// ex:- �T\0\0\0ProtoBufCrossFramework.Test.Shared.DTO.BaseClass, ProtoBufCrossFramework.Test.Shared�{\0\0\0System.Collections.Generic.Dictionary`2[[System.String, mscorlib,%core%],[System.String, mscorlib,%core%]], mscorlib,%core%\u0001\0\0\0�3\0\0\0System.Collections.DictionaryEntry, mscorlib,%core%\a\u0005key1\a\avalue1\0�T\0\0\0System.Collections.Generic.List`1[[System.String, mscorlib,%core%]], mscorlib,%core%\u0001\0\0\0\a\u0006test1\0\u0001\0\0\0\0\0\0\0
        /// Notice how the first character is unicode. Thats because first character's length translated to string turns in to unicode.
        /// Instead we need the byte value itself which in this case is 84 thats the length. Next TypeName length is 123, etc.
        /// 
        /// 1) Find all type names delimiter in bytes.
        /// 2) Get the length of typeNameBytes (the previous byte)
        /// 3) Within that length of that typeNameBytes, count the number of occurences of assembly name.
        /// 4) Adjust the length of typeNameBytes by +(occurences * [len(newAssemblyName) - len(oldAssemblyName)])
        /// 5) Replace old assembly/type name with new assembly/type name.
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        public static byte[] GetBytesForCurrentFramework(byte[] bytes)
        {
            if (!IsCoreAssembly && HasCrossFrameworkRef(bytes))
            {
                bool hasExpandoReference = false, hasAssemblyReference = false;

                // 1) Find all type names delimiter. Like \0\0\0 in "\0\0\0System.Collections.Generic.Dictionary"
                foreach (var position in bytes.Locate(typeNameBytesPattern))
                {
                    // 1a) Get Length of typenames (System.blah.blah...mscorlib..)
                    var currentTypeNameLenBits = new ArraySegment<byte>(bytes, position - 3, 4);
                    var currentTypeNameLen = BitConverter.ToInt32(currentTypeNameLenBits.ToArray(), 0);
                    byte newTypeNameLen = (byte)currentTypeNameLen;

                    // 1b) Get the entire typename ex:-[System.Collections.DictionaryEntry, mscorlib,%core%]
                    var typeNameSegment = new ArraySegment<byte>(bytes, position + 1, currentTypeNameLen);
                    var typeNameArray = typeNameSegment.ToArray();

                    // 3a) Within that length, find occurences of mscorlib/System.Private.CoreLib.
                    newTypeNameLen = GetNewTypeNameLength(newTypeNameLen, typeNameArray, OldAssemblyBytes, NewAssemblyBytes, ref hasAssemblyReference);

                    // 3b) Within that length, find occurences of System.Linq.Expressions/System.Core. 
                    //      This applies to expandoobject
                    newTypeNameLen = GetNewDynamicTypeNameLength(newTypeNameLen, typeNameArray, coreExpandoTypeBytesPattern, fullFwkExpandoTypeBytes, ref hasExpandoReference);

                    #region Commented
                    //var assemblyIndexes = typeNameArray.Locate(OldAssemblyBytes);
                    //if (assemblyIndexes.Length > 0)
                    //{
                    //    // 4) Adjust the length of typeNameBytes by +(occurences * [len(newAssemblyName) - len(oldAssemblyName)])
                    //    byte typeDifferenceLen = (byte)(assemblyIndexes.Length * (NewAssemblyBytes.Length - OldAssemblyBytes.Length));
                    //    newTypeNameLen += typeDifferenceLen;
                    //}

                    // 3b) Within that length, find occurences of System.Linq.Expressions/System.Core.
                    //var expandoIndexes = typeNameArray.Locate(coreExpandoTypeBytes);
                    //if (expandoIndexes.Length > 0)
                    //{
                    //    hasExpandoReference = true;

                    //    // 4) Adjust the length of typeNameBytes by +(occurences * [len(newAssemblyName) - len(oldAssemblyName)])
                    //    byte typeDifferenceLen = (byte)(expandoIndexes.Length * (fullFwkExpandoTypeBytes.Length - coreExpandoTypeBytes.Length));
                    //    newTypeNameLen += typeDifferenceLen;
                    //}
                    #endregion

                    if (newTypeNameLen != 0 && currentTypeNameLen != newTypeNameLen)
                    {
                        //Assign the new length to incoming bytes array.
                        var bitsNewTypeNameLen = BitConverter.GetBytes(newTypeNameLen);
                        bitsNewTypeNameLen.CopyTo(currentTypeNameLenBits.Array, position - 3);
                    }
                }


                // 5a) Replace the assembly name from mscorlib to System.Private.CoreLib or viceversa.
                byte[] bytesForCurrentFramework =
                    bytes.ReplaceBytes(OldAssemblyBytes, NewAssemblyBytes);

                if (hasExpandoReference)
                {
                    // 5b) Replace the assembly name from 
                    //      "System.Dynamic.ExpandoObject, System.Linq.Expressions, Version=4.2.0.0, PublicKeyToken=b03f5f7f11d50a3a".
                    //      to "System.Dynamic.ExpandoObject, System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
                    bytesForCurrentFramework =
                       bytesForCurrentFramework.ReplaceBytes(CoreExpandoTypeBytes, fullFwkExpandoTypeBytes);
                }

                return bytesForCurrentFramework;
            }

            return bytes;
        }

        private static byte GetNewTypeNameLength(byte newTypeNameLen, byte[] typeNameArray, byte[] oldAssemblyBytes, byte[] newAssemblyBytes, ref bool hasReference)
        {
            var assemblyIndexes = typeNameArray.Locate(oldAssemblyBytes);
            if (assemblyIndexes.Length > 0)
            {
                hasReference = true;
                // 4) Adjust the length of typeNameBytes by +(occurences * [len(newAssemblyName) - len(oldAssemblyName)])
                byte typeDifferenceLen = (byte)(assemblyIndexes.Length * (newAssemblyBytes.Length - oldAssemblyBytes.Length));
                newTypeNameLen += typeDifferenceLen;
            }

            return newTypeNameLen;
        }

        private static byte GetNewDynamicTypeNameLength(byte newTypeNameLen, byte[] typeNameArray, byte[] oldAssemblyBytes, byte[] newAssemblyBytes, ref bool hasReference)
        {
            var assemblyIndexes = typeNameArray.Locate(oldAssemblyBytes);
            if (assemblyIndexes.Length > 0)
            {
                var expandoTypeBytes = GetNewTypeExpandoBytes(typeNameArray);

                hasReference = true;

                // 4) Adjust the length of typeNameBytes by +(occurences * [len(newAssemblyName) - len(expandoTypeBytes)])
                byte typeDifferenceLen = (byte)(assemblyIndexes.Length * (newAssemblyBytes.Length - expandoTypeBytes.Length));
                newTypeNameLen += typeDifferenceLen;
            }

            return newTypeNameLen;
        }

        private static byte[] GetNewTypeExpandoBytes(byte[] typeNameArray)
        {
            if(CoreExpandoTypeBytes == null || CoreExpandoTypeBytes.Length == 0)
            {
                // Get string version of typename from bytes
                var typeNameStr = encoding.GetString(typeNameArray);

                // Get expando typename within using Regex.
                var match = expandoRegex.Match(typeNameStr);

                // Get bytes for the expando typename for the future comparisons.
                CoreExpandoTypeBytes = encoding.GetBytes(match.Value);
            }

            return CoreExpandoTypeBytes;
        }

        private static bool HasCrossFrameworkRef(byte[] bytes)
        {
            //var index = IsCoreAssembly ? bytes.FindBytes(fullFwkAssemblyBytes) : bytes.FindBytes(coreAssemblyBytes);

            //return index > -1;

            return bytes.FindBytes(OldAssemblyBytes) > -1;
        }

        private static bool CheckIsCoreFramework()
        {
            var assembly = typeof(System.Runtime.GCSettings).GetTypeInfo().Assembly;
            var assemblyPath = assembly.CodeBase.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            int netCoreAppIndex = Array.IndexOf(assemblyPath, "Microsoft.NETCore.App");

            return netCoreAppIndex > -1;
        }
    }
}
