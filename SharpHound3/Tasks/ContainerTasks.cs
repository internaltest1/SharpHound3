﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SharpHound3.JSON;
using SharpHound3.LdapWrappers;

namespace SharpHound3.Tasks
{
    class ContainerTasks
    {
        internal static ConcurrentDictionary<string, string> GPONameMap = new ConcurrentDictionary<string, string>();

        internal static void BuildGPOCache(string domain)
        {
            var searcher = Helpers.GetDirectorySearcher(domain);

            foreach (var searchResultEntry in searcher.QueryLdap("(&(objectCategory=groupPolicyContainer)(name=*)(gpcfilesyspath=*))", new[] { "displayname", "name" }, SearchScope.Subtree))
            {
                //Remove the brackets for the GPO guid
                var gpoGuid = searchResultEntry.GetProperty("name").ToUpper();
                gpoGuid = gpoGuid.Substring(1, gpoGuid.Length - 2);
                //Grab the display name. If its null, then juse use the GPO guid as the name
                var displayName = searchResultEntry.GetProperty("displayname")?.ToUpper() ?? gpoGuid;
                GPONameMap.TryAdd(gpoGuid, displayName);
            }
        }
        
        internal static LdapWrapper EnumerateContainer(LdapWrapper wrapper)
        {
            if (wrapper is OU ou)
            {
                ProcessOUObject(ou);
            }else if (wrapper is Domain domain)
            {
                ProcessDomainObject(domain);
            }

            return wrapper;
        }

        private static void ProcessDomainObject(Domain domain)
        {
            var searchResult = domain.SearchResult;
            var resolvedLinks = new List<GPLink>();

            var gpLinks = searchResult.GetProperty("gplink");

            if (gpLinks != null)
            {
                foreach (var link in gpLinks.Split(']', '[').Where(l => l.StartsWith("LDAP")))
                {
                    var splitLink = link.Split(';');
                    var distinguishedName = splitLink[0];
                    var status = splitLink[1];

                    //Status 1 and status 3 correspond to disabled/unenforced and disabled/enforced, so filter them out
                    if (status == "1" || status == "3")
                        continue;

                    //If the status is 0, its unenforced, 2 is enforced
                    var enforced = status == "2";

                    var displayName =
                        GPONameMap.GetOrAdd(distinguishedName, key => distinguishedName);

                    resolvedLinks.Add(new GPLink
                    {
                        IsEnforced = enforced,
                        Name = $"{displayName}@{ou.Domain}".ToUpper()
                    });
                }
            }
        }

        private static void ProcessOUObject(OU ou)
        {
            var searchResult = ou.SearchResult;
            var gpOptions = searchResult.GetProperty("gpoptions");

            ou.Properties.Add("blocksinheritance", gpOptions != null && gpOptions == "1");

            var resolvedLinks = new List<GPLink>();

            var gpLinks = searchResult.GetProperty("gplink");
            if (gpLinks != null)
            {
                foreach (var link in gpLinks.Split(']', '[').Where(l => l.StartsWith("LDAP")))
                {
                    var splitLink = link.Split(';');
                    var distinguishedName = splitLink[0];
                    var status = splitLink[1];

                    //Status 1 and status 3 correspond to disabled/unenforced and disabled/enforced, so filter them out
                    if (status == "1" || status == "3")
                        continue;

                    //If the status is 0, its unenforced, 2 is enforced
                    var enforced = status == "2";

                    var displayName =
                        GPONameMap.GetOrAdd(distinguishedName, key => distinguishedName);

                    resolvedLinks.Add(new GPLink
                    {
                        IsEnforced = enforced,
                        Name = $"{displayName}@{ou.Domain}".ToUpper()
                    });
                }
            }

            var users = new List<string>();
            var computers = new List<string>();
            var ous = new List<string>();

            var searcher = Helpers.GetDirectorySearcher(ou.Domain);
            foreach (var containedObject in searcher.QueryLdap(
                "(|(samAccountType=805306368)(samAccountType=805306369)(objectclass=organizationalUnit))", new[]
                {
                    "objectguid", "objectclass", "objectsid", "samaccounttype"
                }, SearchScope.OneLevel, ou.DistinguishedName))
            {
                var type = containedObject.GetLdapType();

                string sid;
                switch (type)
                {
                    case LdapTypeEnum.OU:
                        var guid = containedObject.GetPropertyAsBytes("objectguid");
                        if (guid == null)
                            continue;
                        ous.Add(new Guid(guid).ToString().ToUpper());
                        break;
                    case LdapTypeEnum.Computer:
                        sid = containedObject.GetSid();
                        if (sid == null)
                            continue;
                        computers.Add(sid);
                        break;
                    case LdapTypeEnum.User:
                        sid = containedObject.GetSid();
                        if (sid == null)
                            continue;
                        users.Add(sid);
                        break;
                    default:
                        continue;
                }

                ou.Computers = computers.ToArray();
                ou.Users = users.ToArray();
                ou.ChildOus = ous.ToArray();
            }
        }
    }
}
