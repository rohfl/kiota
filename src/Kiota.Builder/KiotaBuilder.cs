﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Kiota.Builder.Extensions;
using Microsoft.OpenApi.Any;
using System.ComponentModel;

namespace Kiota.Builder
{
    public class KiotaBuilder
    {
        private readonly ILogger<KiotaBuilder> logger;
        private readonly GenerationConfiguration config;

        public KiotaBuilder(ILogger<KiotaBuilder> logger, GenerationConfiguration config)
        {
            this.logger = logger;
            this.config = config;
        }

        public async Task GenerateSDK()
        {
            var sw = new Stopwatch();
            // Step 1 - Read input stream
            string inputPath = config.OpenAPIFilePath;
            sw.Start();
            using var input = await LoadStream(inputPath);
            StopLogAndReset(sw, "step 1 - reading the stream - took");

            // Step 2 - Parse OpenAPI
            sw.Start();
            var doc = CreateOpenApiDocument(input);
            StopLogAndReset(sw, "step 2 - parsing the document - took");

            // Step 3 - Create Uri Space of API
            sw.Start();
            var openApiTree = CreateUriSpace(doc);
            StopLogAndReset(sw, "step 3 - create uri space - took");

            // Step 4 - Create Source Model
            sw.Start();
            var generatedCode = CreateSourceModel(openApiTree);
            StopLogAndReset(sw, "step 4 - create source model - took");

            // Step 5 - RefineByLanguage
            sw.Start();
            ApplyLanguageRefinement(config.Language, generatedCode);
            StopLogAndReset(sw, "step 5 - refine by language - took");

            // Step 6 - Write language source 
            sw.Start();
            await CreateLanguageSourceFilesAsync(config.Language, generatedCode);
            StopLogAndReset(sw, "step 6 - writing files - took");
        }
        private void StopLogAndReset(Stopwatch sw, string prefix) {
            sw.Stop();
            logger.LogDebug($"{prefix} {sw.Elapsed}");
            sw.Reset();
        }


        private async Task<Stream> LoadStream(string inputPath)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            Stream input;
            if (inputPath.StartsWith("http"))
            {
                using var httpClient = new HttpClient();
                input = await httpClient.GetStreamAsync(inputPath);
            }
            else
            {
                input = new FileStream(inputPath, FileMode.Open);
            }
            stopwatch.Stop();
            logger.LogTrace("{timestamp}ms: Read OpenAPI file {file}", stopwatch.ElapsedMilliseconds, inputPath);
            return input;
        }


        public OpenApiDocument CreateOpenApiDocument(Stream input)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            logger.LogTrace("Parsing OpenAPI file");
            var reader = new OpenApiStreamReader();
            var doc = reader.Read(input, out var diag);
            stopwatch.Stop();
            if (diag.Errors.Count > 0)
            {
                logger.LogError("{timestamp}ms: OpenApi Parsing errors", stopwatch.ElapsedMilliseconds, String.Join(Environment.NewLine, diag.Errors.Select(e => e.Message)));
            }
            else
            {
                logger.LogTrace("{timestamp}ms: Parsed OpenAPI successfully. {count} paths found.", stopwatch.ElapsedMilliseconds, doc.Paths.Count);
            }

            return doc;
        }

        /// <summary>
        /// Translate OpenApi PathItems into a tree structure that will define the classes
        /// </summary>
        /// <param name="doc">OpenAPI Document of the API to be processed</param>
        /// <returns>Root node of the API URI space</returns>
        public OpenApiUrlSpaceNode CreateUriSpace(OpenApiDocument doc)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            var node = OpenApiUrlSpaceNode.Create(doc);
            ComponentsReferencesIndex = node.GetComponentsReferenceIndex();

            stopwatch.Stop();
            logger.LogTrace("{timestamp}ms: Created UriSpace tree", stopwatch.ElapsedMilliseconds);
            return node;
        }
        private Dictionary<string, HashSet<OpenApiUrlSpaceNode>> ComponentsReferencesIndex;
        private CodeNamespace rootNamespace;

        /// <summary>
        /// Convert UriSpace of OpenApiPathItems into conceptual SDK Code model 
        /// </summary>
        /// <param name="root">Root OpenApiUriSpaceNode of API to be generated</param>
        /// <returns></returns>
        public CodeNamespace CreateSourceModel(OpenApiUrlSpaceNode root)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            rootNamespace = CodeNamespace.InitRootNamespace();
            var codeNamespace = rootNamespace.AddNamespace(this.config.ClientNamespaceName);
            CreateRequestBuilderClass(codeNamespace, root, root);
            StopLogAndReset(stopwatch, $"{nameof(CreateRequestBuilderClass)}");
            stopwatch.Start();
            MapTypeDefinitions(codeNamespace);
            StopLogAndReset(stopwatch, $"{nameof(MapTypeDefinitions)}");

            logger.LogTrace("{timestamp}ms: Created source model with {count} classes", stopwatch.ElapsedMilliseconds, codeNamespace.GetChildElements(true).Count());

            return rootNamespace;
        }

        /// <summary>
        /// Manipulate CodeDOM for language specific issues
        /// </summary>
        /// <param name="language"></param>
        /// <param name="generatedCode"></param>
        public void ApplyLanguageRefinement(GenerationLanguage language, CodeNamespace generatedCode)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            ILanguageRefiner.Refine(language, generatedCode);

            stopwatch.Stop();
            logger.LogDebug("{timestamp}ms: Language refinement applied", stopwatch.ElapsedMilliseconds);
        }

        /// <summary>
        /// Iterate through Url Space and create request builder classes for each node in the tree
        /// </summary>
        /// <param name="root">Root node of URI space from the OpenAPI described API</param>
        /// <returns>A CodeNamespace object that contains request builder classes for the Uri Space</returns>

        public async Task CreateLanguageSourceFilesAsync(GenerationLanguage language, CodeNamespace generatedCode)
        {
            LanguageWriter languageWriter;
            switch (language)
            {
                case GenerationLanguage.CSharp:
                    languageWriter = new CSharpWriter(this.config.OutputPath, this.config.ClientNamespaceName);
                    break;
                case GenerationLanguage.Java:
                    languageWriter = new JavaWriter(this.config.OutputPath, this.config.ClientNamespaceName);
                    break;
                case GenerationLanguage.TypeScript:
                    languageWriter = new TypeScriptWriter(this.config.OutputPath, this.config.ClientNamespaceName);
                    break;
                default:
                    throw new InvalidEnumArgumentException($"{language} language currently not supported.");
            }

            var stopwatch = new Stopwatch();
            stopwatch.Start();
            await CodeRenderer.RenderCodeNamespaceToFilePerClassAsync(languageWriter, generatedCode);
            stopwatch.Stop();
            logger.LogTrace("{timestamp}ms: Files written to {path}", stopwatch.ElapsedMilliseconds, config.OutputPath);
        }
        private static readonly string requestBuilderSuffix = "RequestBuilder";

        /// <summary>
        /// Create a CodeClass instance that is a request builder class for the OpenApiUrlSpaceNode
        /// </summary>
        private void CreateRequestBuilderClass(CodeNamespace currentNamespace, OpenApiUrlSpaceNode currentNode, OpenApiUrlSpaceNode rootNode)
        {
            // Determine Class Name
            CodeClass codeClass;
            var isRootClientClass = String.IsNullOrEmpty(currentNode.GetIdentifier());
            if (isRootClientClass)
            {
                codeClass = new CodeClass(currentNamespace) { 
                    Name = this.config.ClientClassName,
                    ClassKind = CodeClassKind.RequestBuilder,
                    Description = "The main entry point of the SDK, exposes the configuration and the fluent API."
                };
            }
            else
            {
                var className = currentNode.GetClassName(requestBuilderSuffix);
                codeClass = new CodeClass((currentNode.DoesNodeBelongToItemSubnamespace() ? currentNamespace.EnsureItemNamespace() : currentNamespace)) {
                    Name = className, 
                    ClassKind = CodeClassKind.RequestBuilder,
                    Description = currentNode.PathItem?.Description ?? currentNode.PathItem?.Summary ?? $"Builds and executes requests for operations under {currentNode.Path}",
                };
            }
            var targetNS = currentNode.DoesNodeBelongToItemSubnamespace() ? currentNamespace.EnsureItemNamespace() : currentNamespace;
            codeClass = targetNS.AddClass(codeClass).First();

            logger.LogTrace("Creating class {class}", codeClass.Name);

            // Add properties for children
            foreach (var child in currentNode.Children)
            {
                var propIdentifier = child.Value.GetClassName();
                var propType = propIdentifier + requestBuilderSuffix;
                if (child.Value.IsParameter())
                {
                    var prop = CreateIndexer($"{propIdentifier}-indexer", propType, codeClass, child.Value);
                    codeClass.SetIndexer(prop);
                }
                else if (child.Value.IsFunction())
                {
                    // Don't support functions for the moment
                }
                else
                {
                    var prop = CreateProperty(propIdentifier, propType, codeClass, kind: CodePropertyKind.RequestBuilder); // we should add the type definition here but we can't as it might not have been generated yet
                    codeClass.AddProperty(prop);
                }
            }

            // Add methods for Operations
            if (currentNode.HasOperations())
            {
                foreach(var operation in currentNode.PathItem.Operations)
                    CreateOperationMethods(rootNode, currentNode, operation.Key, operation.Value, codeClass);
            }
            CreatePathManagement(codeClass, currentNode, isRootClientClass);
           
            Parallel.ForEach(currentNode.Children.Values, childNode =>
            {
                var targetNamespaceName = childNode.GetNodeNamespaceFromPath(this.config.ClientNamespaceName);
                var targetNamespace = rootNamespace.FindNamespaceByName(targetNamespaceName) ?? rootNamespace.AddNamespace(targetNamespaceName);
                CreateRequestBuilderClass(targetNamespace, childNode, rootNode);
            });
        }

        private void CreatePathManagement(CodeClass currentClass, OpenApiUrlSpaceNode currentNode, bool isRootClientClass) {
            var pathProperty = new CodeProperty(currentClass) {
                Access = AccessModifier.Private,
                Name = "pathSegment",
                DefaultValue = isRootClientClass ? $"\"{this.config.ApiRootUrl}\"" : (currentNode.IsParameter() ? "\"\"" : $"\"/{currentNode.Segment}\""),
                ReadOnly = true,
                Description = "Path segment to use to build the URL for the current request builder"
            };
            pathProperty.Type = new CodeType(pathProperty) {
                Name = "string",
                IsNullable = false,
                IsExternal = true,
            };
            currentClass.AddProperty(pathProperty);

            var currentPathProperty = new CodeProperty(currentClass) {
                Name = "currentPath",
                Description = "Current path for the request"
            };
            currentPathProperty.Type = new CodeType(currentPathProperty) {
                Name = "string",
                IsExternal = true,
            };
            currentClass.AddProperty(currentPathProperty);

            var httpCoreProperty = new CodeProperty(currentClass) {
                Name = "httpCore",
                Description = "Core service to use to execute the requests"
            };
            httpCoreProperty.Type = new CodeType(httpCoreProperty) {
                Name = "IHttpCore",
                IsExternal = true,
            };
            currentClass.AddProperty(httpCoreProperty);

            var serializerFactoryProperty = new CodeProperty(currentClass) {
                Name = "serializerFactory",
                Description = "Factory to use to get a serializer for payload serialization"
            };
            serializerFactoryProperty.Type = new CodeType(serializerFactoryProperty) {
                Name = "ISerializationWriterFactory",
                IsExternal = true,
            };
            currentClass.AddProperty(serializerFactoryProperty);
        }
        private static Func<CodeClass, int> shortestNamespaceOrder = (x) => x.Parent.Name.Split('.').Length;
        /// <summary>
        /// Remaps definitions to custom types so they can be used later in generation or in refiners
        /// </summary>
        private void MapTypeDefinitions(CodeElement codeElement) {
            var unmappedTypes = GetUnmappedTypeDefinitions(codeElement).Distinct();
            
            var unmappedTypesWithNoName = unmappedTypes.Where(x => string.IsNullOrEmpty(x.Name)).ToList();
            
            unmappedTypesWithNoName.ForEach(x => {
                logger.LogWarning($"Type with empty name and parent {x.Parent.Name}");
            });

            var unmappedTypesWithName = unmappedTypes.Except(unmappedTypesWithNoName);

            var unmappedRequestBuilderTypes = unmappedTypesWithName
                                    .Where(x => 
                                    x.Parent is CodeProperty property && property.PropertyKind == CodePropertyKind.RequestBuilder || x.Parent is CodeIndexer)
                                    .ToList();
            
            Parallel.ForEach(unmappedRequestBuilderTypes, x => {
                var parentNS = x.Parent.Parent.Parent as CodeNamespace;
                x.TypeDefinition = parentNS.FindChildrenByName<CodeClass>(x.Name)
                                            .OrderBy(shortestNamespaceOrder)
                                            .FirstOrDefault();
                // searching down first because most request builder properties on a request builder are just sub paths on the API
                if(x.TypeDefinition == null) {
                    parentNS = parentNS.Parent as CodeNamespace;
                    x.TypeDefinition = parentNS
                        .FindNamespaceByName($"{parentNS.Name}.{x.Name.Substring(0, x.Name.Length - requestBuilderSuffix.Length).ToFirstCharacterLowerCase()}")
                        .FindChildrenByName<CodeClass>(x.Name)
                        .OrderBy(shortestNamespaceOrder)
                        .FirstOrDefault();
                    // in case of the .item namespace, going to the parent and then down to the target by convention
                    // this avoid getting the wrong request builder in case we have multiple request builders with the same name in the parent branch
                    // in both cases we always take the uppermost item (smaller numbers of segments in the namespace name)
                }
            });

            Parallel.ForEach(unmappedTypesWithName.Except(unmappedRequestBuilderTypes).GroupBy(x => x.Name), x => {
                var definition = rootNamespace.FindChildByName<ITypeDefinition>(x.First().Name) as CodeElement;
                if(definition != null)
                    foreach(var type in x) {
                        type.TypeDefinition = definition;
                    }
            });
        }
        private static IEnumerable<CodeType> filterUnmappedTypeDefitions(IEnumerable<CodeTypeBase> source) =>
        source.OfType<CodeType>()
                .Union(source
                        .OfType<CodeUnionType>()
                        .SelectMany(x => x.Types))
                .Where(x => !x.IsExternal && x.TypeDefinition == null);
        private IEnumerable<CodeType> GetUnmappedTypeDefinitions(CodeElement codeElement) {
            var childElementsUnmappedTypes = codeElement.GetChildElements(true).SelectMany(x => GetUnmappedTypeDefinitions(x));
            switch(codeElement) {
                case CodeMethod method:
                    return filterUnmappedTypeDefitions(method.Parameters.Select(x => x.Type)).Union(childElementsUnmappedTypes);
                case CodeProperty property:
                    return filterUnmappedTypeDefitions(new CodeTypeBase[] {property.Type}).Union(childElementsUnmappedTypes);
                case CodeIndexer indexer:
                    return filterUnmappedTypeDefitions(new CodeTypeBase[] {indexer.ReturnType}).Union(childElementsUnmappedTypes);
                default:
                    return childElementsUnmappedTypes;
            }
        }
        private CodeIndexer CreateIndexer(string childIdentifier, string childType, CodeClass codeClass, OpenApiUrlSpaceNode currentNode)
        {
            var prop = new CodeIndexer(codeClass)
            {
                Name = childIdentifier,
                Description = $"Gets an item from the {currentNode.GetNodeNamespaceFromPath(this.config.ClientNamespaceName)} collection",
            };
            prop.IndexType = new CodeType(prop) { Name = "string", IsExternal = true, };
            prop.ReturnType = new CodeType(prop)
            {
                Name = childType
            };
            logger.LogTrace("Creating indexer {name}", childIdentifier);
            return prop;
        }

        private CodeProperty CreateProperty(string childIdentifier, string childType, CodeClass codeClass, string defaultValue = null, OpenApiSchema typeSchema = null, CodeElement typeDefinition = null, CodePropertyKind kind = CodePropertyKind.Custom)
        {
            var isCollection = typeSchema?.Type?.Equals("array", StringComparison.CurrentCultureIgnoreCase) ?? false;
            var propertyName = childIdentifier;
            this.config.PropertiesPrefixToStrip.ForEach(x => propertyName = propertyName.Replace(x, string.Empty));
            var prop = new CodeProperty(codeClass)
            {
                Name = propertyName,
                DefaultValue = defaultValue,
                PropertyKind = kind,
                Description = typeSchema?.Description,
            };
            if(propertyName != childIdentifier)
                prop.SerializationName = childIdentifier;
            var typeName = childType;
            var isExternal = false;
            if("string".Equals(typeName, StringComparison.OrdinalIgnoreCase) && "date-time".Equals(typeSchema?.Format, StringComparison.OrdinalIgnoreCase)) {
                isExternal = true;
                typeName = "DateTimeOffset";
            } else if ("double".Equals(typeSchema?.Format, StringComparison.OrdinalIgnoreCase)) {
                isExternal = true;
                typeName = "double";
            }
            prop.Type = new CodeType(prop) {
                Name = typeName,
                TypeDefinition = typeDefinition,
                CollectionKind = isCollection ? CodeType.CodeTypeCollectionKind.Complex : default,
                IsExternal = isExternal,
            };
            logger.LogTrace("Creating property {name} of {type}", prop.Name, prop.Type.Name);
            return prop;
        }

        private const string requestBodyJsonContentType = "application/json"; //TODO: this is temporary, we should handle other content types like yaml, grpc, xml...
        private const string requestBodyBinaryContentType = "application/octet-stream";
        private void CreateOperationMethods(OpenApiUrlSpaceNode rootNode, OpenApiUrlSpaceNode currentNode, OperationType operationType, OpenApiOperation operation, CodeClass parentClass)
        {
            var parameterClass = CreateOperationParameter(currentNode, operationType, operation, parentClass);

            var schema = operation.GetResponseSchema();
            var method = (HttpMethod)Enum.Parse(typeof(HttpMethod), operationType.ToString());
            var executorMethod = new CodeMethod(parentClass) {
                Name = operationType.ToString(),
                MethodKind = CodeMethodKind.RequestExecutor,
                HttpMethod = method,
                Description = operation.Description ?? operation.Summary,
            };
            parentClass.AddMethod(executorMethod);
            if (schema != null)
            {
                var returnType = CreateModelClasses(rootNode, currentNode, schema, operation, executorMethod);
                executorMethod.ReturnType = returnType ?? throw new InvalidOperationException("Could not resolve return type for operation");
            } else {
                var returnType = "Entity";//TODO remove this temporary default when the method above handles all cases
                if(operation.Responses.Any(x => x.Key == "204"))
                    returnType = "void";
                else if(operation.Responses.Any(x => x.Value.Content.Keys.Contains(requestBodyBinaryContentType)))
                    returnType = "binary";
                executorMethod.ReturnType = new CodeType(executorMethod) { Name = returnType };
            }

            
            AddRequestBuilderMethodParameters(rootNode, currentNode, operation, parameterClass, executorMethod);

            var handlerParam = new CodeParameter(executorMethod) {
                Name = "responseHandler",
                Optional = true,
                ParameterKind = CodeParameterKind.ResponseHandler,
                Description = "Response handler to use in place of the default response handling provided by the core service"
            };
            handlerParam.Type = new CodeType(handlerParam) { Name = "IResponseHandler", IsExternal = true };
            executorMethod.AddParameter(handlerParam);
            logger.LogTrace("Creating method {name} of {type}", executorMethod.Name, executorMethod.ReturnType);

            var generatorMethod = new CodeMethod(parentClass) {
                Name = $"Create{operationType.ToString().ToFirstCharacterUpperCase()}RequestInfo",
                MethodKind = CodeMethodKind.RequestGenerator,
                IsAsync = false,
                HttpMethod = method,
                Description = operation.Description ?? operation.Summary,
            };
            generatorMethod.ReturnType = new CodeType(generatorMethod) { Name = "RequestInfo", IsNullable = false, IsExternal = true};
            parentClass.AddMethod(generatorMethod);
            AddRequestBuilderMethodParameters(rootNode, currentNode, operation, parameterClass, generatorMethod);
            logger.LogTrace("Creating method {name} of {type}", generatorMethod.Name, generatorMethod.ReturnType);
        }
        private void AddRequestBuilderMethodParameters(OpenApiUrlSpaceNode rootNode, OpenApiUrlSpaceNode currentNode, OpenApiOperation operation, CodeClass parameterClass, CodeMethod method) {
            if (operation.RequestBody?.Content?.ContainsKey(requestBodyJsonContentType) ?? false)
            {
                var requestBodySchema = operation.RequestBody.Content[requestBodyJsonContentType].Schema;
                var requestBodyType = CreateModelClasses(rootNode, currentNode, requestBodySchema, operation, method);
                method.AddParameter(new CodeParameter(method) {
                    Name = "body",
                    Type = requestBodyType,
                    Optional = false,
                    ParameterKind = CodeParameterKind.RequestBody,
                    Description = requestBodySchema.Description
                });
            } else if (operation.RequestBody?.Content?.ContainsKey(requestBodyBinaryContentType) ?? false) {
                var nParam = new CodeParameter(method) {
                    Name = "body",
                    Optional = false,
                    ParameterKind = CodeParameterKind.RequestBody,
                    Description = $"Binary request body"
                };
                nParam.Type = new CodeType(nParam) {
                    Name = "binary",
                    IsExternal = true,
                };
                method.AddParameter(nParam);
            }
            if(parameterClass != null) {
                var qsParam = new CodeParameter(method)
                {
                    Name = "q",
                    Optional = true,
                    ParameterKind = CodeParameterKind.QueryParameter,
                    Description = "Request query parameters"
                };
                qsParam.Type = new CodeType(qsParam) { Name = parameterClass.Name, ActionOf = true, TypeDefinition = parameterClass };
                method.AddParameter(qsParam);
            }
            var headersParam = new CodeParameter(method) {
                Name = "h",
                Optional = true,
                ParameterKind = CodeParameterKind.Headers,
                Description = "Request headers"
            };
            headersParam.Type = new CodeType(headersParam) { Name = "IDictionary<string, string>", ActionOf = true, IsExternal = true };
            method.AddParameter(headersParam);
        }
        private IEnumerable<string> GetAllNamespaceNamesForModelByReferenceId(string referenceId) {
            if(string.IsNullOrEmpty(referenceId)) throw new ArgumentNullException(nameof(referenceId));
            return ComponentsReferencesIndex.TryGetValue(referenceId, out var nodes) ? 
                        nodes.Select(x => x.GetNodeNamespaceFromPath(this.config.ClientNamespaceName)) :
                        Enumerable.Empty<string>();
        }
        private string GetShortestNamespaceNameForModelByReferenceId(string referenceId) {
            if(string.IsNullOrEmpty(referenceId))
                throw new ArgumentNullException(nameof(referenceId));
            
            var potentialNamespaceNamesWithDepth = GetAllNamespaceNamesForModelByReferenceId(referenceId)
                                .Select(x => new Tuple<string, int>(x, x.Count(y => y == '.')))
                                .OrderBy(x => x.Item2);
            var currentShortestCandidate = potentialNamespaceNamesWithDepth.FirstOrDefault();
            if(currentShortestCandidate == null)
                return null;

            var countOfNamespaceNamesMeetingCutoff = potentialNamespaceNamesWithDepth.Count(x => x.Item2 == currentShortestCandidate.Item2);
            if (countOfNamespaceNamesMeetingCutoff == 1)
                return currentShortestCandidate.Item1;
            else if(countOfNamespaceNamesMeetingCutoff > 1) {
                return currentShortestCandidate.Item1.Substring(0, currentShortestCandidate.Item1.LastIndexOf('.'));
            } else 
                throw new InvalidOperationException($"could not find a shortest namespace name for reference id {referenceId}");
        }
        private CodeType CreateModelClassAndType(OpenApiUrlSpaceNode rootNode, OpenApiUrlSpaceNode currentNode, OpenApiSchema schema, OpenApiOperation operation, CodeElement parentElement, CodeNamespace codeNamespace, string classNameSuffix = "") {
            var className = currentNode.GetClassName(operation: operation, suffix: classNameSuffix);
            var codeDeclaration = AddModelDeclarationIfDoesntExit(rootNode, currentNode, schema, operation, className, codeNamespace, parentElement);
            return new CodeType(parentElement) {
                TypeDefinition = codeDeclaration,
                Name = className,
            };
        }
        private CodeTypeBase CreateModelClasses(OpenApiUrlSpaceNode rootNode, OpenApiUrlSpaceNode currentNode, OpenApiSchema schema, OpenApiOperation operation, CodeElement parentElement)
        {
            var originalReference = schema?.Reference;
            var originalReferenceId = originalReference?.Id;
            var codeNamespace = parentElement.GetImmediateParentOfType<CodeNamespace>();
            
            if (originalReference == null) { // Inline schema, i.e. specific to the Operation
                return CreateModelClassAndType(rootNode, currentNode, schema, operation, parentElement, codeNamespace, "Response");
            } else if(schema?.AllOf?.Any() ?? false) {
                var allOfs = schema.AllOf.FlattenEmptyEntries(x => x.AllOf);
                var lastSchema = allOfs.Last();
                CodeElement codeDeclaration = null;
                string className = string.Empty;
                foreach(var currentSchema in allOfs) {
                    var isLastSchema = currentSchema == lastSchema;
                    var shortestNamespaceName = currentSchema.Reference == null ? currentNode.GetNodeNamespaceFromPath(this.config.ClientNamespaceName) : GetShortestNamespaceNameForModelByReferenceId(currentSchema.Reference.Id);
                    var shortestNamespace = rootNamespace.FindNamespaceByName(shortestNamespaceName);
                    if(shortestNamespace == null)
                        shortestNamespace = rootNamespace.AddNamespace(shortestNamespaceName);
                    className = isLastSchema ? currentNode.GetClassName(operation: operation) : currentSchema.GetClassName();
                    codeDeclaration = AddModelDeclarationIfDoesntExit(rootNode, currentNode, currentSchema, operation, className, shortestNamespace, parentElement, codeDeclaration as CodeClass, true);
                }

                return new CodeType(parentElement) {
                    TypeDefinition = codeDeclaration,
                    Name = className,
                };
            } else if((schema?.AnyOf?.Any() ?? false) || (schema?.OneOf?.Any() ?? false)) {
                var schemas = schema.AnyOf.Union(schema.OneOf);
                var unionType = new CodeUnionType(parentElement) {
                    Name = currentNode.GetClassName(operation: operation, suffix: "Response"),
                };
                foreach(var currentSchema in schemas) {
                    var shortestNamespaceName = currentSchema.Reference == null ? currentNode.GetNodeNamespaceFromPath(this.config.ClientNamespaceName) : GetShortestNamespaceNameForModelByReferenceId(currentSchema.Reference.Id);
                    var shortestNamespace = rootNamespace.FindNamespaceByName(shortestNamespaceName);
                    if(shortestNamespace == null)
                        shortestNamespace = rootNamespace.AddNamespace(shortestNamespaceName);
                    var className = currentSchema.GetClassName();
                    var codeDeclaration = AddModelDeclarationIfDoesntExit(rootNode, currentNode, currentSchema, operation, className, shortestNamespace, parentElement);
                    unionType.AddType(new CodeType(unionType) {
                        TypeDefinition = codeDeclaration,
                        Name = className,
                    });
                }
                return unionType;
            } else if(schema?.Type?.Equals("object") ?? false) {
                // referenced schema, no inheritance or union type
                return CreateModelClassAndType(rootNode, currentNode, schema, operation, parentElement, codeNamespace);
            }
            else throw new InvalidOperationException("un handled case, might be object type or array type");
            // object type array of object are technically already handled in properties but if we have a root with those we might be missing some cases here
        }
        private CodeElement GetExistingDeclaration(bool checkInAllNamespaces, CodeNamespace currentNamespace, OpenApiUrlSpaceNode currentNode, string declarationName) {
            var searchNameSpace = GetSearchNamespace(checkInAllNamespaces, currentNode, currentNamespace);
            return searchNameSpace.FindChildByName<ITypeDefinition>(declarationName, checkInAllNamespaces) as CodeElement;
        }
        private CodeNamespace GetSearchNamespace(bool checkInAllNamespaces, OpenApiUrlSpaceNode currentNode, CodeNamespace currentNamespace) {
            if(checkInAllNamespaces) return rootNamespace;
            else if (currentNode.DoesNodeBelongToItemSubnamespace()) return rootNamespace.EnsureItemNamespace();
            else return currentNamespace;
        }
        private CodeElement AddModelDeclarationIfDoesntExit(OpenApiUrlSpaceNode rootNode, OpenApiUrlSpaceNode currentNode, OpenApiSchema schema, OpenApiOperation operation, string declarationName, CodeNamespace currentNamespace, CodeElement parentElement, CodeClass inheritsFrom = null, bool checkInAllNamespaces = false) {
            var existingDeclaration = GetExistingDeclaration(checkInAllNamespaces, currentNamespace, currentNode, declarationName);
            if(existingDeclaration == null) // we can find it in the components
            {
                if(schema.Enum.Any()) {
                    var newEnum = new CodeEnum(currentNamespace) { 
                        Name = declarationName,
                        Options = schema.Enum.OfType<OpenApiString>().Select(x => x.Value).Where(x => !"null".Equals(x)).ToList(),//TODO set the flag property
                        Description = currentNode.PathItem.Description ?? currentNode.PathItem.Summary,
                    };
                    return currentNamespace.AddEnum(newEnum).First();
                } else {
                    if(inheritsFrom == null && schema.AllOf.Count > 1) { //the last is always the current class, we want the one before the last as parent
                        var parentSchema = schema.AllOf.Except(new OpenApiSchema[] {schema.AllOf.Last()}).FirstOrDefault();
                        if(parentSchema != null)
                            inheritsFrom = AddModelDeclarationIfDoesntExit(rootNode, currentNode, parentSchema, operation, parentSchema.GetClassName(), currentNamespace, parentElement, null, true) as CodeClass;
                    }
                    var newClass = currentNamespace.AddClass(new CodeClass(currentNamespace) {
                        Name = declarationName,
                        ClassKind = CodeClassKind.Model,
                        Description = currentNode.PathItem.Description ?? currentNode.PathItem.Summary
                    }).First();
                    if(inheritsFrom != null) {
                        var declaration = newClass.StartBlock as CodeClass.Declaration;
                        declaration.Inherits = new CodeType(declaration) { TypeDefinition = inheritsFrom, Name = inheritsFrom.Name };
                    }
                    CreatePropertiesForModelClass(rootNode, currentNode, schema, operation, currentNamespace, newClass, parentElement);
                    return newClass;
                }
            } else
                return existingDeclaration;
        }
        private const string OpenApiObjectType = "object";
        private void CreatePropertiesForModelClass(OpenApiUrlSpaceNode rootNode, OpenApiUrlSpaceNode currentNode, OpenApiSchema schema, OpenApiOperation operation, CodeNamespace ns, CodeClass model, CodeElement parent) {
            AddSerializationMembers(model, schema?.AdditionalPropertiesAllowed ?? false);
            if(schema?.Properties?.Any() ?? false)
            {
                model.AddProperty(schema
                                    .Properties
                                    .Select(x => {
                                        var propertyDefinitionSchema = x.Value.Items ?? x.Value;
                                        var className = x.Value.GetClassName();
                                        CodeElement definition = default;
                                        if(!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(propertyDefinitionSchema?.Reference?.Id)) {
                                            var shortestNamespaceName = GetShortestNamespaceNameForModelByReferenceId(propertyDefinitionSchema.Reference.Id);
                                            var targetNamespace = string.IsNullOrEmpty(shortestNamespaceName) ? ns : 
                                                                    (rootNamespace.FindNamespaceByName(shortestNamespaceName) ?? rootNamespace.AddNamespace(shortestNamespaceName));
                                            definition = AddModelDeclarationIfDoesntExit(rootNode, currentNode, propertyDefinitionSchema, operation, className, targetNamespace, parent, null, true);
                                        }
                                        return CreateProperty(x.Key, className ?? x.Value.Type, model, typeSchema: x.Value, typeDefinition: definition);
                                    })
                                    .ToArray());
            }
            else if(schema?.AllOf?.Any(x => x?.Type?.Equals(OpenApiObjectType) ?? false) ?? false)
                CreatePropertiesForModelClass(rootNode, currentNode, schema.AllOf.Last(x => x.Type.Equals(OpenApiObjectType)), operation, ns, model, parent);
        }
        private const string deserializeFieldsPropName = "DeserializeFields";
        private const string serializeMethodName = "Serialize";
        private const string additionalDataPropName = "AdditionalData";
        private static void AddSerializationMembers(CodeClass model, bool includeAdditionalProperties) {
            var serializationPropsType = $"IDictionary<string, Action<{model.Name.ToFirstCharacterUpperCase()}, IParseNode>>";
            if(!model.ContainsMember(deserializeFieldsPropName)) {
                var deserializeProp = new CodeProperty(model) {
                    Name = deserializeFieldsPropName,
                    PropertyKind = CodePropertyKind.Deserializer,
                    Access = AccessModifier.Public,
                    ReadOnly = true,
                    Description = "The serialization information for the current model"
                };
                deserializeProp.Type = new CodeType(deserializeProp) {
                    Name = serializationPropsType,
                    IsNullable = false,
                    IsExternal = true,
                };
                model.AddProperty(deserializeProp);
            }
            if(!model.ContainsMember(serializeMethodName)) {
                var serializeMethod = new CodeMethod(model) {
                    Name = serializeMethodName,
                    MethodKind = CodeMethodKind.Serializer,
                    IsAsync = false,
                    Description = $"Serialiazes information the current object",
                };
                serializeMethod.ReturnType = new CodeType(serializeMethod) { Name = "void", IsNullable = false, IsExternal = true };
                var parameter = new CodeParameter(serializeMethod) {
                    Name = "writer",
                    Description = "Serialization writer to use to serialize this model"
                };
                parameter.Type = new CodeType(parameter) { Name = "ISerializationWriter", IsExternal = true };
                serializeMethod.AddParameter(parameter);
                
                model.AddMethod(serializeMethod);
            }
            if(!model.ContainsMember(additionalDataPropName) &&
                includeAdditionalProperties && 
                !(model.GetGreatestGrandparent(model)?.ContainsMember(additionalDataPropName) ?? false)) {
                // we don't want to add the property if the parent already has it
                var additionalDataProp = new CodeProperty(model) {
                    Name = additionalDataPropName,
                    Access = AccessModifier.Public,
                    DefaultValue = "new Dictionary<string, object>()",
                    PropertyKind = CodePropertyKind.AdditionalData,
                    Description = "Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.",
                    ReadOnly = true,
                };
                additionalDataProp.Type = new CodeType(additionalDataProp) {
                    Name = "IDictionary<string, object>",
                    IsNullable = false,
                    IsExternal = true,
                };
                model.AddProperty(additionalDataProp);
            }
        }
        private CodeClass CreateOperationParameter(OpenApiUrlSpaceNode node, OperationType operationType, OpenApiOperation operation, CodeClass parentClass)
        {
            var parameters = node.PathItem.Parameters.Union(operation.Parameters).Where(p => p.In == ParameterLocation.Query);
            if(parameters.Any()) {
                var parameterClass = new CodeClass(parentClass)
                {
                    Name = operationType.ToString() + "QueryParameters",
                    ClassKind = CodeClassKind.QueryParameters,
                    Description = operation.Description ?? operation.Summary
                };
                foreach (var parameter in parameters)
                {
                    var prop = new CodeProperty(parameterClass)
                    {
                        Name = FixQueryParameterIdentifier(parameter),
                        Description = parameter.Description,
                    };
                    prop.Type = new CodeType(prop)
                    {
                        Name = parameter.Schema.Items?.Type ?? parameter.Schema.Type,
                        CollectionKind = parameter.Schema.Type.Equals("array", StringComparison.OrdinalIgnoreCase) ? CodeType.CodeTypeCollectionKind.Array : default
                    };

                    if (!parameterClass.ContainsMember(parameter.Name))
                    {
                        parameterClass.AddProperty(prop);
                    }
                    else
                    {
                        logger.LogWarning("Ignoring duplicate parameter {name}", parameter.Name);
                    }
                }

                return parameterClass;
            } else return null;
        }

        private static string FixQueryParameterIdentifier(OpenApiParameter parameter)
        {
            // Replace with regexes pulled from settings that are API specific

            return parameter.Name.Replace("$","").ToCamelCase();
        }
    }
}
