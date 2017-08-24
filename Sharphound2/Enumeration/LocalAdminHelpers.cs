﻿using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.DirectoryServices.Protocols;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Sharphound2.OutputObjects;
using SearchScope = System.DirectoryServices.Protocols.SearchScope;

namespace Sharphound2.Enumeration
{
    internal class ApiFailedException : Exception { }

    internal class SystemDownException : Exception { }

    internal static class LocalAdminHelpers
    {
        private static Cache _cache;
        private static Utils _utils;
        private static readonly Regex SectionRegex = new Regex(@"^\[(.+)\]", RegexOptions.Compiled);
        private static readonly Regex KeyRegex = new Regex(@"(.+?)\s*=(.*)", RegexOptions.Compiled);

        private static readonly string[] Props =
            {"samaccountname", "samaccounttype", "dnshostname", "serviceprincipalname", "distinguishedname"};

        private static readonly string[] GpoProps =
            {"samaccounttype", "dnshostname", "distinguishedname", "serviceprincipalname"};

        private static readonly string[] GpLinkProps = {"distinguishedname"};

        private static readonly string[] AdminProps = {"samaccountname", "dnshostname", "distinguishedname", "samaccounttype"};

        public static void Init()
        {
            _cache = Cache.Instance;
            _utils = Utils.Instance;
        }

        
        public static void GetSamAdmins(string target)
        {
            //Huge thanks to Simon Mourier on for putting me on the right track
            //https://stackoverflow.com/questions/31464835/how-to-programatically-check-the-password-must-meet-complexity-requirements-gr/31748252

            var sid = new SecurityIdentifier("S-1-5-32");
            var sidbytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(sidbytes, 0);

            var server = new UNICODE_STRING(target);
            SamConnect(server, out IntPtr serverHandle, SamAccessMasks.SAM_SERVER_CONNECT | SamAccessMasks.SAM_SERVER_LOOKUP_DOMAIN | SamAccessMasks.SAM_SERVER_ENUMERATE_DOMAINS, false);
            string machineSid;
            try
            {
                SamLookupDomainInSamServer(serverHandle, server, out IntPtr temp);
                machineSid = new SecurityIdentifier(temp).Value;
            }
            catch
            {
                machineSid = "DUMMYSTRINGSHOULDNOTMATCH";
            }

            Console.WriteLine(machineSid);
            
            SamOpenDomain(serverHandle, DomainAccessMask.Lookup | DomainAccessMask.ListAccounts, sidbytes, out IntPtr domainHandle);
            SamOpenAlias(domainHandle, AliasOpenFlags.ListMembers, 544, out IntPtr aliasHandle);
            
            SamGetMembersInAlias(aliasHandle, out IntPtr members, out int count);
            if (count == 0) return;

            var grabbedSids = new IntPtr[count];
            Marshal.Copy(members, grabbedSids, 0, count);

            var sids = new string[count];

            for (var i = 0; i < count; i++)
            {
                sids[i] = new SecurityIdentifier(grabbedSids[i]).Value;
            }

            LsaOpenPolicy(server, default(OBJECT_ATTRIBUTES),
                LsaOpenMask.ViewLocalInfo | LsaOpenMask.LookupNames, out IntPtr policyHandle);
            
            LsaLookupSids(policyHandle, count, members, out IntPtr domainList,
                out IntPtr nameList);

            var iter = nameList;
            var translatedNames = new LSA_TRANSLATED_NAMES[count];
            for (var i = 0; i < count; i++)
            {
                translatedNames[i] = (LSA_TRANSLATED_NAMES) Marshal.PtrToStructure(iter, typeof(LSA_TRANSLATED_NAMES));
                iter = (IntPtr) (iter.ToInt64() + Marshal.SizeOf(typeof(LSA_TRANSLATED_NAMES)));
            }

            var lsaDomainList =
                (LSA_REFERENCED_DOMAIN_LIST) (Marshal.PtrToStructure(domainList, typeof(LSA_REFERENCED_DOMAIN_LIST)));

            var trustInfos = new LSA_TRUST_INFORMATION[lsaDomainList.count];
            iter = lsaDomainList.domains;
            for (var i = 0; i < lsaDomainList.count; i++)
            {
                trustInfos[i] = (LSA_TRUST_INFORMATION) Marshal.PtrToStructure(iter, typeof(LSA_TRUST_INFORMATION));
                iter = (IntPtr) (iter.ToInt64() + Marshal.SizeOf(typeof(LSA_TRUST_INFORMATION)));
            }

            for (var i = 0; i < translatedNames.Length; i++)
            {
                var x = translatedNames[i];
                Console.WriteLine($"{sids[i]} - {trustInfos[x.domainIndex].name}\\{x.name}");
            }
        }

        #region LSA Imports
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern NTSTATUS LsaLookupSids(
            IntPtr policyHandle,
            int count,
            IntPtr enumBuffer,
            out IntPtr domainList,
            out IntPtr nameList
        );

        [DllImport("advapi32.dll")]
        private static extern NTSTATUS LsaOpenPolicy(
            UNICODE_STRING server,
            OBJECT_ATTRIBUTES objectAttributes,
            LsaOpenMask desiredAccess,
            out IntPtr policyHandle
        );

        [DllImport("advapi32.dll")]
        private static extern NTSTATUS LsaFreeMemory(
            IntPtr buffer
        );

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LSA_TRUST_INFORMATION
        {
            internal LSA_UNICODE_STRING name;
            internal IntPtr sid;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LSA_UNICODE_STRING
        {
            internal ushort length;
            internal ushort maxLen;
            [MarshalAs(UnmanagedType.LPWStr)] internal string name;

            public override string ToString()
            {
                return $"{name.Substring(0, length / 2)}";
            }
        }

        private struct LSA_TRANSLATED_NAMES
        {
            internal SID_NAME_USE use;
            internal LSA_UNICODE_STRING name;
            internal int domainIndex;
        }

        private struct LSA_REFERENCED_DOMAIN_LIST
        {
            public int count;
            public IntPtr domains;
        }
        #endregion

        #region SAMR Imports

        [DllImport("samlib.dll", CharSet = CharSet.Unicode)]
        private static extern NTSTATUS SamLookupDomainInSamServer(
            IntPtr serverHandle,
            UNICODE_STRING name,
            out IntPtr sid);

        [DllImport("samlib.dll", CharSet = CharSet.Unicode)]
        private static extern NTSTATUS SamGetMembersInAlias(
            IntPtr aliasHandle,
            out IntPtr members,
            out int count);

        [DllImport("samlib.dll", CharSet = CharSet.Unicode)]
        private static extern NTSTATUS SamOpenAlias(
            IntPtr domainHandle,
            AliasOpenFlags desiredAccess,
            int aliasId,
            out IntPtr aliasHandle
        );


        [DllImport("samlib.dll", CharSet = CharSet.Unicode)]
        private static extern NTSTATUS SamConnect(
            UNICODE_STRING serverName,
            out IntPtr serverHandle,
            SamAccessMasks desiredAccess,
            bool objectAttributes
            );

        [DllImport("samlib.dll", CharSet = CharSet.Unicode)]
        private static extern NTSTATUS SamEnumerateAliasesInDomain(
            IntPtr domainHandle,
            ref int enumerationContext,
            out IntPtr buffer,
            int preferredMaxLen,
            out int count
            );

        [DllImport("samlib.dll", CharSet = CharSet.Unicode)]
        private static extern NTSTATUS SamOpenAlias(
            IntPtr domainHandle, 
            SamAliasFlags desiredAccess,
            int aliasId,
            out IntPtr aliasHandle
        );

        [DllImport("samlib.dll", CharSet = CharSet.Unicode)]
        private static extern NTSTATUS SamOpenDomain(
            IntPtr serverHandle,
            DomainAccessMask desiredAccess,
            byte[] DomainSid,
            out IntPtr DomainHandle
        );

        [Flags]
        private enum AliasOpenFlags
        {
            AddMember = 0x1,
            RemoveMember = 0x2,
            ListMembers = 0x4,
            ReadInfo = 0x8,
            WriteAccount = 0x10,
            AllAccess = 0xf001f,
            Read = 0x20004,
            Write = 0x20013,
            Execute = 0x20008
        }

        [Flags]
        private enum LsaOpenMask
        {
            ViewLocalInfo = 0x1,
            ViewAuditInfo = 0x2,
            GetPrivateInfo = 0x4,
            TrustAdmin = 0x8,
            CreateAccount = 0x10,
            CreateSecret = 0x20,
            CreatePrivilege = 0x40,
            SetDefaultQuotaLimits = 0x80,
            SetAuditRequirements = 0x100,
            AuditLogAdmin = 0x200,
            ServerAdmin = 0x400,
            LookupNames = 0x800,
            Notification = 0x1000
        }

        [Flags]
        private enum DomainAccessMask
        {
            ReadPasswordParameters = 0x1,
            WritePasswordParameters = 0x2,
            ReadOtherParameters = 0x4,
            WriteOtherParameters = 0x8,
            CreateUser = 0x10,
            CreateGroup = 0x20,
            CreateAlias = 0x40,
            GetAliasMembership = 0x80,
            ListAccounts = 0x100,
            Lookup = 0x200,
            AdministerServer = 0x400,
            AllAccess = 0xf07ff,
            Read = 0x20084,
            Write = 0x2047A,
            Execute = 0x20301
        }

        [Flags]
        private enum SamAliasFlags
        {
            AddMembers = 0x1,
            RemoveMembers = 0x2,
            ListMembers = 0x4,
            ReadInfo = 0x8,
            WriteAccount = 0x10,
            AllAccess = 0xf001f,
            Read = 0x20004,
            Write = 0x20013,
            Execute = 0x20008
        }

        [Flags]
        private enum SamAccessMasks
        {
            SAM_SERVER_CONNECT = 0x1,
            SAM_SERVER_SHUTDOWN = 0x2,
            SAM_SERVER_INITIALIZE = 0x4,
            SAM_SERVER_CREATE_DOMAINS = 0x8,
            SAM_SERVER_ENUMERATE_DOMAINS = 0x10,
            SAM_SERVER_LOOKUP_DOMAIN = 0x20,
            SAM_SERVER_ALL_ACCESS = 0xf003f,
            SAM_SERVER_READ = 0x20010,
            SAM_SERVER_WRITE = 0x2000e,
            SAM_SERVER_EXECUTE = 0x20021
        }

        private struct SAM_RID_ENUMERATION
        {
            public uint RelativeId;
            public UNICODE_STRING name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING : IDisposable
        {
            public ushort Length;
            public ushort MaximumLength;
            private IntPtr Buffer;

            public UNICODE_STRING(string s)
                : this()
            {
                if (s != null)
                {
                    Length = (ushort)(s.Length * 2);
                    MaximumLength = (ushort)(Length + 2);
                    Buffer = Marshal.StringToHGlobalUni(s);
                }
            }

            public void Dispose()
            {
                if (Buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(Buffer);
                    Buffer = IntPtr.Zero;
                }
            }

            public override string ToString()
            {
                return Buffer != IntPtr.Zero ? Marshal.PtrToStringUni(Buffer) : null;
            }
        }

        private struct OBJECT_ATTRIBUTES : IDisposable
        {
            public void Dispose()
            {
                if (objectName == IntPtr.Zero) return;
                Marshal.DestroyStructure(objectName, typeof(UNICODE_STRING));
                Marshal.FreeHGlobal(objectName);
                objectName = IntPtr.Zero;
            }
            public int len;
            public IntPtr rootDirectory;
            public uint attribs;
            public IntPtr sid;
            public IntPtr qos;
            private IntPtr objectName;
            public UNICODE_STRING ObjectName;
        }

        private enum NTSTATUS
        {
            STATUS_SUCCESS = 0x0,
            STATUS_MORE_ENTRIES = 0x105,
            STATUS_INVALID_HANDLE = unchecked((int)0xC0000008),
            STATUS_INVALID_PARAMETER = unchecked((int)0xC000000D),
            STATUS_ACCESS_DENIED = unchecked((int)0xC0000022),
            STATUS_OBJECT_TYPE_MISMATCH = unchecked((int)0xC0000024),
            STATUS_NO_SUCH_DOMAIN = unchecked((int)0xC00000DF),
        }
        #endregion

        public static IEnumerable<LocalAdmin> GetGpoAdmins(SearchResultEntry entry, string domainName)
        {
            const string targetSid = "S-1-5-32-544__Members";

            var displayName = entry.GetProp("displayname");
            var name = entry.GetProp("name");
            var path = entry.GetProp("gpcfilesyspath");
            

            if (displayName == null || name == null || path == null)
            {
                yield break;
            }

            var template = $"{path}\\MACHINE\\Microsoft\\Windows NT\\SecEdit\\GptTmpl.inf";
            var currentSection = string.Empty;
            var resolvedList = new List<MappedPrincipal>();

            if (!File.Exists(template))
                yield break;

            using (var reader = new StreamReader(template))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var sMatch = SectionRegex.Match(line);
                    if (sMatch.Success)
                    {
                        currentSection = sMatch.Captures[0].Value.Trim();
                    }

                    if (!currentSection.Equals("[Group Membership]"))
                    {
                        continue;
                    }

                    var kMatch = KeyRegex.Match(line);

                    if (!kMatch.Success)
                        continue;

                    var n = kMatch.Groups[1].Value;
                    var v = kMatch.Groups[2].Value;

                    if (!n.Contains(targetSid))
                        continue;

                    v = v.Trim();
                    var members = v.Split(',');


                    foreach (var m in members)
                    {
                        var member = m.Trim('*');
                        string sid;
                        if (!member.StartsWith("S-1-", StringComparison.CurrentCulture))
                        {
                            try
                            {
                                sid = new NTAccount(domainName, m).Translate(typeof(SecurityIdentifier)).Value;
                            }
                            catch
                            {
                                sid = null;
                            }
                        }
                        else
                        {
                            sid = member;
                        }

                        if (sid == null)
                            continue;

                        var domain = _utils.SidToDomainName(sid) ?? domainName;
                        var resolvedPrincipal = _utils.UnknownSidTypeToDisplay(sid, domain, Props);
                        if (resolvedPrincipal != null)
                            resolvedList.Add(resolvedPrincipal);
                    }
                }
            }

            foreach (var ouObject in _utils.DoSearch($"(gplink=*{name}*)", SearchScope.Subtree, GpLinkProps, domainName))
            {
                var adspath = ouObject.DistinguishedName;

                foreach (var compEntry in _utils.DoSearch("(objectclass=computer)", SearchScope.Subtree, GpoProps,
                    domainName, adspath))
                {
                    var samAccountType = compEntry.GetProp("samaccounttype");
                    if (samAccountType == null || samAccountType != "805306369")
                        continue;

                    var server = compEntry.ResolveBloodhoundDisplay();

                    foreach (var user in resolvedList)
                    {
                        yield return new LocalAdmin
                        {
                            ObjectName = user.PrincipalName,
                            ObjectType = user.ObjectType,
                            Server = server
                        };
                    }
                }
            }
        }

        public static List<LocalAdmin> GetLocalAdmins(string target, string group, string domainName, string domainSid)
        {
            var toReturn = new List<LocalAdmin>();
            try
            {
                toReturn = LocalGroupApi(target, group, domainName, domainSid);
                return toReturn;
            }
            catch (SystemDownException)
            {
                return toReturn;
            }
            catch (ApiFailedException)
            {
                Utils.Verbose($"LocalGroup: Falling back to WinNT Provider for {target}");
                try
                {
                    toReturn = LocalGroupWinNt(target, group);
                    return toReturn;
                }
                catch
                {
                    return toReturn;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return toReturn;
            }
        }
        
        public static List<LocalAdmin> LocalGroupWinNt(string target, string group)
        {
            var members = new DirectoryEntry($"WinNT://{target}/{group},group");
            var localAdmins = new List<LocalAdmin>();
            try
            {
                foreach (var member in (System.Collections.IEnumerable)members.Invoke("Members"))
                {
                    using (var m = new DirectoryEntry(member))
                    {
                        //Convert sid bytes to a string
                        var sidstring = new SecurityIdentifier(m.GetSid(), 0).ToString();
                        string type;
                        switch (m.SchemaClassName)
                        {
                            case "Group":
                                type = "group";
                                break;
                            case "User":
                                //If its a user but the name ends in $, it's actually a computer (probably)
                                type = m.Properties["Name"][0].ToString().EndsWith("$", StringComparison.Ordinal) ? "computer" : "user";
                                break;
                            default:
                                type = "group";
                                break;
                        }

                        //Start by checking the cache
                        if (!_cache.GetMapValue(sidstring, type, out string adminName))
                        {
                            //Get the domain from the SID
                            var domainName = _utils.SidToDomainName(sidstring);

                            //Search for the object in AD
                            var entry = _utils
                                .DoSearch($"(objectsid={sidstring})", SearchScope.Subtree, AdminProps, domainName)
                                .DefaultIfEmpty(null).FirstOrDefault();

                            //If it's not null, we have an object, yay! Otherwise, meh
                            if (entry != null)
                            {
                                adminName = entry.ResolveBloodhoundDisplay();
                                _cache.AddMapValue(sidstring, type, adminName);
                            }
                            else
                            {
                                adminName = null;
                            }
                        }

                        if (adminName != null)
                        {
                            localAdmins.Add(new LocalAdmin { ObjectName = adminName, ObjectType = type, Server = target });
                        }
                    }
                }
            }
            catch (COMException)
            {
                //You can get a COMException, so just return a blank array
                return localAdmins;
            }

            return localAdmins;
        }

        public static List<LocalAdmin> LocalGroupApi(string target, string group, string domainName, string domainSid)
        {
            const int queryLevel = 2;
            var resumeHandle = IntPtr.Zero;
            var machineSid = "DUMMYSTRING";

            var LMI2 = typeof(LOCALGROUP_MEMBERS_INFO_2);
            
            var returnValue = NetLocalGroupGetMembers(target, group, queryLevel, out IntPtr ptrInfo, -1, out int entriesRead, out int _, resumeHandle);

            //Return value of 1722 indicates the system is down, so no reason to fallback to WinNT
            if (returnValue == 1722)
            {
                throw new SystemDownException();
            }

            //If its not 0, something went wrong, but we can fallback to WinNT provider. Throw an exception
            if (returnValue != 0)
            {
                throw new ApiFailedException();
            }

            var toReturn = new List<LocalAdmin>();

            if (entriesRead <= 0) return toReturn;

            var iter = ptrInfo;
            var list = new List<API_Encapsulator>();

            //Loop through the data and save them into a list for processing
            for (var i = 0; i < entriesRead; i++)
            {
                var data = (LOCALGROUP_MEMBERS_INFO_2)Marshal.PtrToStructure(iter, LMI2);
                ConvertSidToStringSid(data.lgrmi2_sid, out string sid);
                list.Add(new API_Encapsulator
                {
                    Lgmi2 = data,
                    sid = sid
                });
                iter = (IntPtr)(iter.ToInt64() + Marshal.SizeOf(LMI2));
            }
            
            NetApiBufferFree(ptrInfo);
            //Try and determine the machine sid
            
            foreach (var data in list)
            {
                if (data.sid == null)
                {
                    continue;
                }

                //If the sid ends with -500 and doesn't start with the DomainSID, there's a very good chance we've identified the RID500 account
                //Take the machine sid from there. If we don't find it, we use a dummy string
                if (!data.sid.EndsWith("-500", StringComparison.Ordinal) ||
                    data.sid.StartsWith(domainSid, StringComparison.Ordinal)) continue;
                machineSid = new SecurityIdentifier(data.sid).AccountDomainSid.Value;
                break;
            }
            
            foreach (var data in list)
            {
                if (data.sid == null)
                    continue;
                var objectName = data.Lgmi2.lgrmi2_domainandname;
                if (objectName.Split('\\').Last().Equals(""))
                {
                    //Sometimes we get weird objects that are just a domain name with no user behind it.
                    continue;
                }

                if (data.sid.StartsWith(machineSid, StringComparison.Ordinal))
                {
                    //This should filter out local accounts
                    continue;
                }

                string type;
                switch (data.Lgmi2.lgrmi2_sidusage)
                {
                    case SID_NAME_USE.SidTypeUser:
                        type = "user";
                        break;
                    case SID_NAME_USE.SidTypeGroup:
                        type = "group";
                        break;
                    case SID_NAME_USE.SidTypeComputer:
                        type = "computer";
                        break;
                    case SID_NAME_USE.SidTypeWellKnownGroup:
                        type = "wellknown";
                        break;
                    default:
                        type = null;
                        break;
                }

                //I have no idea what would cause this condition
                if (type == null)
                {
                    continue;
                }

                if (objectName.EndsWith("$", StringComparison.Ordinal))
                {
                    type = "computer";
                }
                
                var resolved = _utils.SidToDisplay(data.sid, _utils.SidToDomainName(data.sid), AdminProps, type);
                if (resolved == null)
                {
                    continue;
                }

                toReturn.Add(new LocalAdmin
                {
                    ObjectName = resolved,
                    ObjectType = type,
                    Server = target
                });
            }
            return toReturn;
        }

        #region pinvoke-imports
        [DllImport("NetAPI32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetLocalGroupGetMembers(
            [MarshalAs(UnmanagedType.LPWStr)] string servername,
            [MarshalAs(UnmanagedType.LPWStr)] string localgroupname,
            int level,
            out IntPtr bufptr,
            int prefmaxlen,
            out int entriesread,
            out int totalentries,
            IntPtr resume_handle);

        [DllImport("Netapi32.dll", SetLastError = true)]
        private static extern int NetApiBufferFree(IntPtr buff);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct LOCALGROUP_MEMBERS_INFO_2
        {
            public IntPtr lgrmi2_sid;
            public SID_NAME_USE lgrmi2_sidusage;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lgrmi2_domainandname;
        }

        public class API_Encapsulator
        {
            public LOCALGROUP_MEMBERS_INFO_2 Lgmi2 { get; set; }
            public string sid;
        }

        public enum SID_NAME_USE
        {
            SidTypeUser = 1,
            SidTypeGroup,
            SidTypeDomain,
            SidTypeAlias,
            SidTypeWellKnownGroup,
            SidTypeDeletedAccount,
            SidTypeInvalid,
            SidTypeUnknown,
            SidTypeComputer
        }

        [DllImport("advapi32", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool ConvertSidToStringSid(IntPtr pSid, out string strSid);
        #endregion 
    }
}
