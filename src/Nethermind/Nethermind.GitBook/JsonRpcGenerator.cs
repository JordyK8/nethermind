using System.IO;
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Nethermind.JsonRpc.Modules;
using System.Text;
using System.Threading.Tasks;
using Nethermind.GitBook.Extensions;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;

namespace Nethermind.GitBook
{
    public class JsonRpcGenerator
    {
        private readonly MarkdownGenerator _markdownGenerator;

        public JsonRpcGenerator(MarkdownGenerator markdownGenerator)
        {
           _markdownGenerator = markdownGenerator; 
        }

        public void Generate()
        {
            string docsDir = DocsDirFinder.FindJsonRpc();
            List<Type> rpcTypes = GetRpcModules();

            foreach (Type rpcType in rpcTypes)
            {
                GenerateDocFileContent(rpcType, docsDir);
            }
        }

        private List<Type> GetRpcModules()
        {
            Assembly assembly = Assembly.Load("Nethermind.JsonRpc");
            List<Type> jsonRpcModules = new List<Type>();

            foreach (Type type in assembly.GetTypes()
                    .Where(t => typeof(IModule).IsAssignableFrom(t))
                    .Where(t => t.IsInterface && t != typeof(IModule)))
            {
                jsonRpcModules.Add(type);
            }

            return jsonRpcModules;
        }

        private void GenerateDocFileContent(Type rpcType, string docsDir)
        {
            StringBuilder docBuilder = new StringBuilder();

            string moduleName = rpcType.Name.Substring(1).Replace("Module", "").ToLower();
            MethodInfo[] moduleMethods = rpcType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            List<Type> rpcTypesToDescribe;

            docBuilder.AppendLine(@$"#{moduleName}");
            docBuilder.AppendLine();

            foreach (MethodInfo method in moduleMethods)
            {
                JsonRpcMethodAttribute attribute = method.GetCustomAttribute<JsonRpcMethodAttribute>();
                bool isImplemented = attribute == null || attribute.IsImplemented;

                if (!isImplemented)
                {
                    continue;
                }

                string methodName = method.Name.Substring(method.Name.IndexOf('_'));
                docBuilder.AppendLine(@$"##{moduleName}\{methodName}");
                docBuilder.AppendLine();
                docBuilder.AppendLine(@$"{attribute?.Description ?? "_description missing_"} ");
                docBuilder.AppendLine();
                _markdownGenerator.OpenTabs(docBuilder);
                _markdownGenerator.CreateTab(docBuilder, "Request");
                docBuilder.AppendLine(@"### **Parameters**");
                docBuilder.AppendLine();

                ParameterInfo[] parameters = method.GetParameters();
                rpcTypesToDescribe = new List<Type>();

                if (parameters.Length == 0)
                {
                    docBuilder.AppendLine("_None_");
                }
                else
                {
                    docBuilder.AppendLine("| Parameter name | Type |");
                    docBuilder.AppendLine("| :--- | :--- |");
                    string rpcParameterType;
                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        rpcParameterType = GetJsonRpcType(parameter.ParameterType, rpcTypesToDescribe);
                        docBuilder.AppendLine($"| {parameter.Name} | `{rpcParameterType}` |");
                    }
                }

                _markdownGenerator.CloseTab(docBuilder);
                docBuilder.AppendLine();
                _markdownGenerator.CreateTab(docBuilder, "Response");
                docBuilder.AppendLine(@$"### Return type");
                docBuilder.AppendLine();

                Type returnType = GetTypeFromWrapper(method.ReturnType);
                string returnRpcType = GetJsonRpcType(returnType, rpcTypesToDescribe);

                docBuilder.AppendLine(@$"`{returnRpcType}`");

                _markdownGenerator.CloseTab(docBuilder);

                if (rpcTypesToDescribe.Count != 0)
                {
                    docBuilder.AppendLine();
                    _markdownGenerator.CreateTab(docBuilder, "Object definitions");
                    AddRpcObjectsDescription(docBuilder, rpcTypesToDescribe);
                    _markdownGenerator.CloseTab(docBuilder);
                }

                _markdownGenerator.CloseTabs(docBuilder);
                docBuilder.AppendLine();
            }

            string rpcModuleFile = Directory.GetFiles(docsDir, $"{moduleName}.md", SearchOption.AllDirectories).First();

            string fileContent = docBuilder.ToString();
            File.WriteAllText(rpcModuleFile, fileContent);
        }

        private string GetJsonRpcType(Type type, List<Type> rpcTypesToDescribe)
        {
            if (type.IsNullable())
            {
                type = Nullable.GetUnderlyingType(type);
            }

            string rpcType = ReplaceType(type);

            if (rpcType.Equals($"{type.Name} object") && rpcTypesToDescribe != null)
            {
                rpcTypesToDescribe.Add(type);
                AdditionalPropertiesToDescribe(type, rpcTypesToDescribe);
            }

            return rpcType;
        }

        private string ReplaceType(Type type)
        {
            if (type.Name.Contains("`")) return "Array";
            
            var rpcType = type.Name switch
            {
                "Object" => "Object",
                "Byte" => "Data",
                "Byte[]" => "Data",
                "Byte[][]" => "Data",
                "String" => "String",
                "UInt256" => "Quantity",
                "Address" => "Address",
                "Boolean" => "Boolean",
                "Int32" => "Quantity",
                "Int32[]" => "Array",
                "Int64" => "Quantity",
                "Int64&" => "Quantity",
                "Keccak" => "Hash",
                "String[]" => "Array",
                "Bloom" => "Bloom Object",
                _ => $"{type.Name} object",
            };
            return rpcType;
        }
        
        private void AddRpcObjectsDescription(StringBuilder rpcModuleBuilder, List<Type> rpcTypesToDescribe)
        {
            rpcModuleBuilder.AppendLine(@$"### Objects definition");

            foreach (var rpcType in rpcTypesToDescribe.Distinct().Where(rpcType => ReplaceType(rpcType).Contains("object")))
            {
                rpcModuleBuilder.AppendLine();
                rpcModuleBuilder.AppendLine(@$"`{rpcType.Name}`");
                rpcModuleBuilder.AppendLine();

                if(rpcType == typeof(BlockParameterType))
                {
                    rpcModuleBuilder.AppendLine("- `Quantity` or `String` (latest, earliest, pending)");
                    rpcModuleBuilder.AppendLine();
                    continue;
                }

                PropertyInfo[] properties = rpcType.GetProperties();

                rpcModuleBuilder.AppendLine("| Fields name | Type |");
                rpcModuleBuilder.AppendLine("| :--- | :--- |");

                string propertyRpcType;
                foreach (PropertyInfo property in properties)
                {
                    propertyRpcType = GetJsonRpcType(property.PropertyType, null);
                    rpcModuleBuilder.AppendLine($"| {property.Name} | `{propertyRpcType}` |");
                }
            }
        }

        private Type GetTypeFromWrapper(Type resultWrapper)
        {
            Type returnType;

            //this is for situation when we have Task<ResultWrapper<T>> and we want to get T out of it 
            if (resultWrapper.GetGenericTypeDefinition() == typeof(Task<>))
            {
                returnType = resultWrapper.GetGenericArguments()[0].GetGenericArguments()[0];
            }
            //when we know that there is only ResultWrapper<T> and we want to return T
            else
            {
                returnType = resultWrapper.GetGenericArguments()[0];
            }

            bool isNullableType = returnType.IsNullable();

            if(isNullableType)
            {
                return Nullable.GetUnderlyingType(returnType);
            }

            return returnType.IsArray ? returnType.GetElementType() : returnType;
        }

        private void AdditionalPropertiesToDescribe(Type type, List<Type> rpcTypesToDescribe)
        {
            PropertyInfo[] properties = type.GetProperties()
                                                        .Where(p => !p.PropertyType.IsPrimitive
                                                         && p.PropertyType != typeof(string) 
                                                         && p.PropertyType != typeof(long)
                                                         && p.PropertyType != typeof(Keccak))
                                                        .ToArray();

            foreach (PropertyInfo property in properties)
            {
                if (property.PropertyType.IsNullable())
                {
                    Type underlyingType = Nullable.GetUnderlyingType(property.PropertyType);
                    rpcTypesToDescribe.Add(underlyingType);
                }
                else
                {
                    rpcTypesToDescribe.Add(property.PropertyType);
                }
            }
        }
    }
}
