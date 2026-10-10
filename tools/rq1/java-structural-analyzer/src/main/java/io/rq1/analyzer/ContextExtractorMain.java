package io.rq1.analyzer;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;
import com.github.javaparser.JavaParser;
import com.github.javaparser.ParserConfiguration;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.body.*;
import com.github.javaparser.ast.expr.*;
import com.github.javaparser.ast.type.Type;

import java.io.BufferedWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.MessageDigest;
import java.util.*;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Stream;

/** Deterministic, depth-one repository context extraction for RQ1 Phase 5. */
public final class ContextExtractorMain {
    private static final String VERSION = "context-extractor-v4";
    private static final Pattern TYPE_TOKEN = Pattern.compile("[A-Za-z_$][A-Za-z0-9_$.]*");
    private static final int MAX_RELATED_TYPES = 16;
    private static final int MAX_RELATED_METHODS = 32;
    private static final int MAX_RELATED_FIELDS = 24;
    private ContextExtractorMain() { }

    public static void main(String[] args) throws Exception {
        Map<String, String> options = parseArgs(args);
        Path projectsRoot = requiredPath(options, "--projects-root");
        Path subjectsPath = requiredPath(options, "--subjects");
        Path output = requiredPath(options, "--output");
        Path errors = output.resolveSibling(output.getFileName() + ".errors.txt");
        Files.createDirectories(output.toAbsolutePath().getParent());
        ObjectMapper mapper = new ObjectMapper().disable(SerializationFeature.FAIL_ON_EMPTY_BEANS);
        JavaParser parser = new JavaParser(new ParserConfiguration()
                .setLanguageLevel(ParserConfiguration.LanguageLevel.BLEEDING_EDGE));
        List<ContextRecord> records = new ArrayList<>();
        List<String> failures = new ArrayList<>();
        Map<Path, ProjectIndex> projectIndexes = new HashMap<>();
        for (String line : Files.readAllLines(subjectsPath, StandardCharsets.UTF_8)) {
            if (line.isBlank()) continue;
            JsonNode subject = mapper.readTree(line);
            try {
                records.add(extract(projectsRoot, subject, parser, projectIndexes));
            } catch (Exception ex) {
                failures.add(subject.path("subjectId").asText("UNKNOWN") + "\t" + ex.getMessage());
            }
        }
        records.sort(Comparator.comparing(ContextRecord::subjectId));
        try (BufferedWriter writer = Files.newBufferedWriter(output, StandardCharsets.UTF_8)) {
            for (ContextRecord record : records) {
                writer.write(mapper.writeValueAsString(record));
                writer.newLine();
            }
        }
        Files.write(errors, failures, StandardCharsets.UTF_8);
        System.out.printf(Locale.ROOT, "contexts=%d errors=%d%n", records.size(), failures.size());
        if (!failures.isEmpty()) System.exit(2);
    }

    private static ContextRecord extract(Path projectsRoot, JsonNode subject, JavaParser parser,
                                         Map<Path, ProjectIndex> projectIndexes) throws Exception {
        String projectId = required(subject, "projectId");
        Path projectRoot = projectsRoot.resolve(projectId).normalize();
        Path source = projectRoot.resolve(required(subject, "sourceFile")).normalize();
        if (!source.startsWith(projectRoot) || !Files.isRegularFile(source))
            throw new IllegalArgumentException("Focal source is outside project root");
        if (!sha256(source).equalsIgnoreCase(required(subject, "sourceSha256")))
            throw new IllegalStateException("Focal source SHA-256 mismatch");

        CompilationUnit focalUnit = parse(parser, source);
        String focalClass = required(subject, "focalClass");
        String signature = required(subject, "focalMethodSignature");
        int startLine = subject.path("startLine").asInt(-1);
        MethodDeclaration focalMethod = focalUnit.findAll(MethodDeclaration.class).stream()
                .filter(m -> fullClassName(focalUnit, m).equals(focalClass))
                .filter(m -> signature(m).equals(signature))
                .filter(m -> m.getBegin().map(p -> p.line).orElse(-1) == startLine)
                .findFirst().orElseThrow(() -> new IllegalStateException("Exact focal method not found"));
        TypeDeclaration<?> focalType = focalMethod.findAncestor(TypeDeclaration.class)
                .orElseThrow(() -> new IllegalStateException("Focal enclosing type not found"));

        ProjectIndex index = projectIndexes.get(projectRoot);
        if (index == null) {
            index = indexProject(projectRoot, parser);
            projectIndexes.put(projectRoot, index);
        }
        List<String> explicitImports = focalUnit.getImports().stream()
                .filter(value -> !value.isStatic() && !value.isAsterisk())
                .map(value -> value.getNameAsString()).toList();
        String focalPackage = focalUnit.getPackageDeclaration().map(p -> p.getNameAsString()).orElse("");
        Map<String, String> variableTypes = variableTypes(focalType, focalMethod);
        List<CalledMethod> resolved = new ArrayList<>();
        List<UnresolvedCall> unresolved = new ArrayList<>();
        Set<String> resolvedKeys = new HashSet<>();
        Set<String> unresolvedKeys = new HashSet<>();
        for (MethodCallExpr call : focalMethod.findAll(MethodCallExpr.class)) {
            Resolution resolution = resolve(call, focalClass, variableTypes, explicitImports, focalPackage, index);
            if (resolution.method != null) {
                MethodInfo method = resolution.method;
                String key = method.focalClass + "#" + method.signature;
                if (!key.equals(focalClass + "#" + signature) && resolvedKeys.add(key))
                    resolved.add(new CalledMethod(method.focalClass, method.signature, method.sourceFile,
                            method.startLine, method.source, resolution.rule));
            } else {
                String text = call.getNameAsString() + "/" + call.getArguments().size() + "@" +
                        call.getBegin().map(p -> p.line).orElse(-1);
                if (unresolvedKeys.add(text))
                    unresolved.add(new UnresolvedCall(call.toString(),
                            call.getBegin().map(p -> p.line).orElse(-1), resolution.rule));
            }
        }
        resolved.sort(Comparator.comparing(CalledMethod::focalClass).thenComparing(CalledMethod::signature));
        unresolved.sort(Comparator.comparingInt(UnresolvedCall::line).thenComparing(UnresolvedCall::expression));

        List<String> imports = focalUnit.getImports().stream().map(Object::toString).map(String::trim).sorted().toList();
        List<String> fields = focalType.getFields().stream().map(Object::toString).map(String::trim).toList();
        List<String> constructors = focalType.getConstructors().stream().map(Object::toString).map(String::trim).toList();
        List<String> focalMethodOverloads = focalType.getMethods().stream()
                .filter(method -> method.getNameAsString().equals(focalMethod.getNameAsString()))
                .map(ContextExtractorMain::signature).distinct().sorted().toList();
        String packageName = focalUnit.getPackageDeclaration().map(p -> p.getNameAsString()).orElse("");
        String typeDeclaration = typeDeclaration(focalType);
        List<RelatedTypeApi> relatedTypeApis = relatedTypeApis(
                focalUnit, focalType, focalMethod, resolved, focalClass, packageName, index);
        return new ContextRecord(required(subject, "subjectId"), required(subject, "split"), projectId,
                required(subject, "sourceFile"), focalClass, signature, required(subject, "sourceSha256"),
                VERSION, "DEPTH_ONE_STATIC_CALLS_AND_RELATED_API_SURFACES_V4", packageName, imports, typeDeclaration,
                fields, constructors, focalMethodOverloads, focalMethod.toString().trim(), resolved,
                relatedTypeApis, unresolved);
    }

    private static ProjectIndex indexProject(Path projectRoot, JavaParser parser) throws Exception {
        List<Path> files;
        try (Stream<Path> stream = Files.walk(projectRoot)) {
            files = stream.filter(Files::isRegularFile)
                    .filter(path -> path.toString().endsWith(".java"))
                    .filter(path -> normalized(path).contains("/src/main/java/"))
                    .sorted().toList();
        }
        List<MethodInfo> methods = new ArrayList<>();
        Map<String, Set<String>> classesBySimpleName = new HashMap<>();
        Map<String, TypeInfo> typesByFullName = new HashMap<>();
        for (Path file : files) {
            CompilationUnit unit;
            try { unit = parse(parser, file); } catch (Exception ignored) { continue; }
            for (TypeDeclaration<?> type : unit.findAll(TypeDeclaration.class)) {
                String full = fullClassName(unit, type);
                classesBySimpleName.computeIfAbsent(type.getNameAsString(), ignored -> new TreeSet<>()).add(full);
                String packageName = unit.getPackageDeclaration().map(p -> p.getNameAsString()).orElse("");
                List<ApiMember> constructors = type.getConstructors().stream()
                        .map(value -> new ApiMember(value.getDeclarationAsString(true, true, true),
                                value.toString().trim(), value.isPrivate(), value.isPublic())).toList();
                List<ApiMember> typeMethods = type.getMethods().stream()
                        .map(value -> new ApiMember(value.getDeclarationAsString(true, true, true),
                                value.toString().trim(), value.isPrivate(), value.isPublic())).toList();
                List<ApiMember> fields = type.getFields().stream()
                        .map(value -> new ApiMember(value.toString().trim(), value.toString().trim(),
                                value.isPrivate(), value.isPublic())).toList();
                if (type instanceof EnumDeclaration enumDeclaration) {
                    List<ApiMember> enumFields = new ArrayList<>(fields);
                    for (EnumConstantDeclaration entry : enumDeclaration.getEntries())
                        enumFields.add(new ApiMember("public static final " + type.getNameAsString() + " "
                                + entry.getNameAsString(), entry.toString().trim(), false, true));
                    fields = enumFields;
                }
                typesByFullName.put(full, new TypeInfo(full, packageName,
                        normalized(projectRoot.relativize(file)), typeDeclaration(type), type.getNameAsString(),
                        type.isPrivate(), type.isPublic(), constructors, typeMethods, fields,
                        constructors.isEmpty()
                                && type instanceof ClassOrInterfaceDeclaration declaration
                                && !declaration.isInterface()
                                && !declaration.isAbstract()));
            }
            for (MethodDeclaration method : unit.findAll(MethodDeclaration.class)) {
                String full = fullClassName(unit, method);
                methods.add(new MethodInfo(full, method.getNameAsString(), method.getParameters().size(),
                        signature(method), normalized(projectRoot.relativize(file)),
                        method.getBegin().map(p -> p.line).orElse(-1), method.toString().trim()));
            }
        }
        methods.sort(Comparator.comparing((MethodInfo m) -> m.focalClass).thenComparing(m -> m.signature));
        return new ProjectIndex(methods, classesBySimpleName, typesByFullName);
    }

    private static List<RelatedTypeApi> relatedTypeApis(
            CompilationUnit focalUnit,
            TypeDeclaration<?> focalType,
            MethodDeclaration focalMethod,
            List<CalledMethod> resolved,
            String focalClass,
            String focalPackage,
            ProjectIndex index) {
        Map<String, Set<String>> reasons = new TreeMap<>();
        List<String> imports = focalUnit.getImports().stream()
                .filter(value -> !value.isStatic() && !value.isAsterisk())
                .map(value -> value.getNameAsString()).toList();
        addTypeReferences(reasons, focalMethod.getType().asString(), "FOCAL_RETURN_TYPE",
                imports, focalPackage, focalClass, index);
        for (Parameter parameter : focalMethod.getParameters())
            addTypeReferences(reasons, parameter.getTypeAsString(), "FOCAL_PARAMETER",
                    imports, focalPackage, focalClass, index);
        for (FieldDeclaration field : focalType.getFields())
            for (VariableDeclarator variable : field.getVariables())
                addTypeReferences(reasons, variable.getTypeAsString(), "FOCAL_FIELD_TYPE",
                        imports, focalPackage, focalClass, index);
        for (ConstructorDeclaration constructor : focalType.getConstructors())
            for (Parameter parameter : constructor.getParameters())
                addTypeReferences(reasons, parameter.getTypeAsString(), "FOCAL_CONSTRUCTOR_PARAMETER",
                        imports, focalPackage, focalClass, index);
        for (VariableDeclarator variable : focalMethod.findAll(VariableDeclarator.class))
            addTypeReferences(reasons, variable.getTypeAsString(), "FOCAL_LOCAL_TYPE",
                    imports, focalPackage, focalClass, index);
        for (ObjectCreationExpr creation : focalMethod.findAll(ObjectCreationExpr.class))
            addTypeReferences(reasons, creation.getTypeAsString(), "FOCAL_OBJECT_CREATION",
                    imports, focalPackage, focalClass, index);
        for (CalledMethod method : resolved)
            if (!method.focalClass().equals(focalClass))
                reasons.computeIfAbsent(method.focalClass(), ignored -> new TreeSet<>()).add("DIRECT_CALL_OWNER");

        List<RelatedTypeApi> result = new ArrayList<>();
        for (Map.Entry<String, Set<String>> selection : reasons.entrySet()) {
            if (result.size() >= MAX_RELATED_TYPES) break;
            TypeInfo info = index.typesByFullName.get(selection.getKey());
            if (info == null || info.isPrivate() || (!info.isPublic() && !info.packageName().equals(focalPackage))) continue;
            boolean samePackage = info.packageName().equals(focalPackage);
            List<String> accessibleConstructors = accessible(info.constructors(), samePackage);
            List<String> accessibleConstructorSources = accessibleSources(info.constructors(), samePackage);
            if (accessibleConstructors.isEmpty() && info.hasImplicitConstructor())
                accessibleConstructors = List.of((info.isPublic() ? "public " : "") + info.simpleName() + "()");
            if (accessibleConstructorSources.isEmpty() && info.hasImplicitConstructor())
                accessibleConstructorSources = List.of((info.isPublic() ? "public " : "")
                        + info.simpleName() + "() { /* implicit constructor; field initializers apply */ }");
            List<String> allMethods = accessible(info.methods(), samePackage);
            List<String> allFields = accessible(info.fields(), samePackage);
            boolean truncated = allMethods.size() > MAX_RELATED_METHODS || allFields.size() > MAX_RELATED_FIELDS;
            result.add(new RelatedTypeApi(info.fullName(), info.sourceFile(), info.declaration(),
                    accessibleConstructors.stream().sorted().toList(),
                    accessibleConstructorSources.stream().sorted().toList(),
                    allMethods.stream().sorted().limit(MAX_RELATED_METHODS).toList(),
                    allFields.stream().sorted().limit(MAX_RELATED_FIELDS).toList(),
                    new ArrayList<>(selection.getValue()), truncated));
        }
        return result;
    }

    private static List<String> accessible(List<ApiMember> members, boolean samePackage) {
        return members.stream()
                .filter(member -> !member.isPrivate() && (member.isPublic() || samePackage))
                .map(ApiMember::declaration).distinct().toList();
    }

    private static List<String> accessibleSources(List<ApiMember> members, boolean samePackage) {
        return members.stream()
                .filter(member -> !member.isPrivate() && (member.isPublic() || samePackage))
                .map(ApiMember::source).filter(source -> !source.isBlank()).distinct().limit(8).toList();
    }

    private static void addTypeReferences(
            Map<String, Set<String>> reasons,
            String typeText,
            String reason,
            List<String> imports,
            String focalPackage,
            String focalClass,
            ProjectIndex index) {
        Matcher matcher = TYPE_TOKEN.matcher(typeText);
        while (matcher.find()) {
            String resolved = resolveTypeName(matcher.group(), imports, focalPackage, index);
            if (resolved != null && !resolved.equals(focalClass))
                reasons.computeIfAbsent(resolved, ignored -> new TreeSet<>()).add(reason);
        }
    }

    private static String resolveTypeName(
            String token,
            List<String> imports,
            String focalPackage,
            ProjectIndex index) {
        if (index.typesByFullName.containsKey(token)) return token;
        String simple = simpleType(token);
        for (String imported : imports)
            if (imported.endsWith("." + simple) && index.typesByFullName.containsKey(imported)) return imported;
        String samePackage = focalPackage.isEmpty() ? simple : focalPackage + "." + simple;
        if (index.typesByFullName.containsKey(samePackage)) return samePackage;
        Set<String> candidates = index.classesBySimpleName.getOrDefault(simple, Set.of());
        return candidates.size() == 1 ? candidates.iterator().next() : null;
    }

    private static Resolution resolve(MethodCallExpr call, String focalClass,
                                      Map<String, String> variableTypes, List<String> imports,
                                      String focalPackage, ProjectIndex index) {
        String name = call.getNameAsString();
        int arity = call.getArguments().size();
        if (call.getScope().isEmpty() || call.getScope().get().isThisExpr())
            return unique(index.methods, focalClass, name, arity, "SAME_TYPE_EXACT_NAME_ARITY");
        Expression scope = call.getScope().get();
        if (scope.isNameExpr()) {
            String token = scope.asNameExpr().getNameAsString();
            String declaredType = variableTypes.get(token);
            if (declaredType != null) {
                String resolvedType = resolveTypeName(declaredType, imports, focalPackage, index);
                if (resolvedType != null)
                    return unique(index.methods, resolvedType, name, arity,
                            "DECLARED_VARIABLE_TYPE_EXACT_NAME_ARITY");
                return new Resolution(null, "UNRESOLVED_AMBIGUOUS_DECLARED_TYPE");
            }
            Set<String> classes = index.classesBySimpleName.getOrDefault(simpleType(token), Set.of());
            if (classes.size() == 1)
                return unique(index.methods, classes.iterator().next(), name, arity,
                        "STATIC_TYPE_EXACT_NAME_ARITY");
        }
        return new Resolution(null, "UNRESOLVED_NO_SAFE_STATIC_RULE");
    }

    private static Resolution unique(List<MethodInfo> methods, String clazz, String name, int arity, String rule) {
        List<MethodInfo> candidates = methods.stream()
                .filter(m -> m.focalClass.equals(clazz) && m.name.equals(name) && m.arity == arity).toList();
        return candidates.size() == 1 ? new Resolution(candidates.get(0), rule)
                : new Resolution(null, candidates.isEmpty() ? "UNRESOLVED_NO_PROJECT_METHOD" : "UNRESOLVED_OVERLOAD_AMBIGUITY");
    }

    private static Map<String, String> variableTypes(TypeDeclaration<?> type, MethodDeclaration method) {
        Map<String, String> result = new HashMap<>();
        for (FieldDeclaration field : type.getFields())
            for (VariableDeclarator variable : field.getVariables()) result.put(variable.getNameAsString(), variable.getTypeAsString());
        for (Parameter parameter : method.getParameters()) result.put(parameter.getNameAsString(), parameter.getTypeAsString());
        for (VariableDeclarator variable : method.findAll(VariableDeclarator.class))
            result.put(variable.getNameAsString(), variable.getTypeAsString());
        return result;
    }

    private static String simpleType(String value) {
        String result = value.replaceAll("<.*>", "").replace("[]", "").replace("...", "").trim();
        int dot = result.lastIndexOf('.');
        return dot < 0 ? result : result.substring(dot + 1);
    }

    private static String typeDeclaration(TypeDeclaration<?> type) {
        String kind = type instanceof ClassOrInterfaceDeclaration declaration && declaration.isInterface() ? "interface" :
                type instanceof EnumDeclaration ? "enum" :
                type instanceof AnnotationDeclaration ? "@interface" : "class";
        String modifiers = type.getModifiers().stream().map(Object::toString).reduce("", String::concat).trim();
        return (modifiers.isEmpty() ? "" : modifiers + " ") + kind + " " + type.getNameAsString();
    }

    private static CompilationUnit parse(JavaParser parser, Path path) throws Exception {
        return parser.parse(path).getResult().orElseThrow(() -> new IllegalStateException("Java parse failed: " + path));
    }

    private static String fullClassName(CompilationUnit unit, MethodDeclaration method) {
        return method.findAncestor(TypeDeclaration.class).map(type -> fullClassName(unit, type)).orElse("");
    }

    private static String fullClassName(CompilationUnit unit, TypeDeclaration<?> type) {
        List<String> names = new ArrayList<>();
        TypeDeclaration<?> current = type;
        while (true) {
            names.add(current.getNameAsString());
            Optional<TypeDeclaration> parent = current.findAncestor(TypeDeclaration.class);
            if (parent.isEmpty()) break;
            current = parent.get();
        }
        Collections.reverse(names);
        String pkg = unit.getPackageDeclaration().map(p -> p.getNameAsString()).orElse("");
        // Keep the same canonical nested-type representation used by the
        // subject and target extractors. Java source writes Outer.Inner, but
        // the frozen focal-class identity uses the JVM-style Outer$Inner.
        return (pkg.isEmpty() ? "" : pkg + ".") + String.join("$", names);
    }

    private static String signature(MethodDeclaration method) {
        return method.getNameAsString() + "(" + method.getParameters().stream()
                .map(parameter -> normalizedType(parameter.getType(), parameter.isVarArgs()))
                .reduce((left, right) -> left + "," + right).orElse("") + ")";
    }

    private static String normalizedType(Type type, boolean varArgs) {
        // Match Main's frozen subject-signature contract exactly. In
        // particular, JavaParser preserves the space in wildcard bounds
        // ("? extends T"); stripping it makes valid frozen methods disappear.
        String text = type.asString();
        return varArgs ? text + "..." : text;
    }

    private static String normalized(Path path) { return path.toString().replace('\\', '/'); }
    private static String required(JsonNode node, String name) {
        String value = node.path(name).asText();
        if (value.isBlank()) throw new IllegalArgumentException("Missing " + name);
        return value;
    }
    private static Path requiredPath(Map<String, String> args, String name) {
        String value = args.get(name);
        if (value == null || value.isBlank()) throw new IllegalArgumentException("Missing " + name);
        return Path.of(value).toAbsolutePath().normalize();
    }
    private static Map<String, String> parseArgs(String[] args) {
        Map<String, String> result = new HashMap<>();
        for (int i = 0; i < args.length; i += 2) {
            if (i + 1 >= args.length) throw new IllegalArgumentException("Missing value for " + args[i]);
            result.put(args[i], args[i + 1]);
        }
        return result;
    }
    private static String sha256(Path path) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(path));
        return HexFormat.of().formatHex(digest);
    }

    private record ProjectIndex(List<MethodInfo> methods, Map<String, Set<String>> classesBySimpleName,
                                Map<String, TypeInfo> typesByFullName) { }
    private record ApiMember(String declaration, String source, boolean isPrivate, boolean isPublic) { }
    private record TypeInfo(String fullName, String packageName, String sourceFile, String declaration,
                            String simpleName, boolean isPrivate, boolean isPublic,
                            List<ApiMember> constructors, List<ApiMember> methods, List<ApiMember> fields,
                            boolean hasImplicitConstructor) { }
    private record MethodInfo(String focalClass, String name, int arity, String signature,
                              String sourceFile, int startLine, String source) { }
    private record Resolution(MethodInfo method, String rule) { }
    public record CalledMethod(String focalClass, String signature, String sourceFile, int startLine,
                               String source, String resolutionRule) { }
    public record RelatedTypeApi(String className, String sourceFile, String typeDeclaration,
                                 List<String> accessibleConstructors, List<String> accessibleConstructorSources,
                                 List<String> accessibleMethods,
                                 List<String> accessibleFields, List<String> selectionReasons,
                                 boolean truncated) { }
    public record UnresolvedCall(String expression, int line, String reason) { }
    public record ContextRecord(String subjectId, String split, String projectId, String sourceFile,
                                String focalClass, String focalMethodSignature, String sourceSha256,
                                String extractorVersion, String retrievalRule, String packageName,
                                List<String> imports, String enclosingTypeDeclaration, List<String> fields,
                                List<String> constructors, List<String> focalMethodOverloadSignatures,
                                String focalMethodSource,
                                List<CalledMethod> directlyCalledProjectMethods,
                                List<RelatedTypeApi> relatedTypeApis,
                                List<UnresolvedCall> unresolvedCalls) { }
}
