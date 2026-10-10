package io.rq1.analyzer;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;
import com.github.javaparser.JavaParser;
import com.github.javaparser.ParserConfiguration;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.Node;
import com.github.javaparser.ast.body.ClassOrInterfaceDeclaration;
import com.github.javaparser.ast.body.ConstructorDeclaration;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.body.TypeDeclaration;
import com.github.javaparser.ast.expr.BinaryExpr;
import com.github.javaparser.ast.expr.ConditionalExpr;
import com.github.javaparser.ast.expr.MethodCallExpr;
import com.github.javaparser.ast.expr.ObjectCreationExpr;
import com.github.javaparser.ast.stmt.*;

import java.io.BufferedWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.MessageDigest;
import java.util.*;
import java.util.stream.Stream;

public final class Main {
    private static final Set<String> EXCLUDED_DIRS = Set.of(
            ".git", "target", "build", "bin", "obj", ".gradle", ".idea",
            // SF110 ships EvoSuite-generated tests outside src/test. They are
            // evaluation artifacts, not production focal-method candidates.
            "evosuite-tests", "test-lib");
    private static final List<String> EXTERNAL_MARKERS = List.of(
            "java.net.", "javax.servlet", "jakarta.servlet", "java.sql.",
            "javax.swing", "java.awt", "javafx", "socket", "httpclient",
            "datasource", "entitymanager", "jdbc", "mongodb",
            "http://", "https://", "request.builder", ".newcall(",
            "preparedstatement", "resultset", "connection ", ".createstatement(",
            ".executequery(", ".executeupdate(", ".commit(", ".rollback(",
            "graphics", "keyevent", "actionevent", "mouseevent", "jframe",
            "jpanel", "swingutilities", "repaint(", "serversocket", ".accept(",
            ".readline(", "writer.println(", "writer.flush(", "multipartfile", "amazons3",
            "jedis ", "jedis.", "jedispool");
    private static final List<String> NONDETERMINISTIC_MARKERS = List.of(
            "system.currenttimemillis", "system.nanotime", "new random(",
            "math.random(", "thread.sleep", "system.in", "scanner(system.in",
            "keyboard.next(", "keyboard.nextline(", "keyboard.nextint(");
    private static final List<String> RUNTIME_CONTAINER_MARKERS = List.of(
            "org.eclipse.", "org.osgi.", "eclipsecontext", "bundlecontext",
            "frameworkutil", "platform.getbundle", "plugin.getdefault",
            "abstractuiplugin", "iplugin", "pluginregistry", "extensionregistry",
            "display.getdefault", "display.getcurrent", "swt.", "swtbot",
            "selectionevent", "selectionchangedevent", "doubleclickevent",
            "checkstatechangedevent", "windowsevent", "uievent",
            "java.awt.dnd", "droptarget", ".gui.", "javax.swing.", "javafx.",
            "resourcesplugin", "iworkspace", "iproject", "ifile", "ifolder",
            "android.app.", "android.content.", "androidx.", "springapplication.run",
            "applicationcontext", "servletcontext", "java.library.path",
            "system.loadlibrary", "system.load(", "jade.core.", "jade.lang.",
            "jade.wrapper.", "jade.proto.");

    private Main() { }

    public static void main(String[] args) throws Exception {
        Map<String, String> options = parseArgs(args);
        Path projectsRoot = options.containsKey("--repository-root")
                ? requiredPath(options, "--repository-root").getParent()
                : requiredPath(options, "--projects-root");
        Path repositoryRoot = options.containsKey("--repository-root")
                ? requiredPath(options, "--repository-root")
                : null;
        Path output = requiredPath(options, "--output");
        String revision = options.getOrDefault("--revision", "UNVERSIONED");
        Files.createDirectories(output.toAbsolutePath().getParent());

        ParserConfiguration configuration = new ParserConfiguration()
                .setLanguageLevel(ParserConfiguration.LanguageLevel.BLEEDING_EDGE);
        JavaParser parser = new JavaParser(configuration);
        ObjectMapper mapper = new ObjectMapper().disable(SerializationFeature.FAIL_ON_EMPTY_BEANS);

        List<Candidate> candidates = new ArrayList<>();
        List<String> errors = new ArrayList<>();
        List<Path> projectDirectories = repositoryRoot == null
                ? listProjectDirectories(projectsRoot)
                : List.of(repositoryRoot);
        for (Path project : projectDirectories) {
                scanProject(projectsRoot, project, revision, parser, candidates, errors);
        }
        candidates.sort(Comparator.comparing(Candidate::projectId)
                .thenComparing(Candidate::focalClass)
                .thenComparing(Candidate::focalMethodSignature)
                .thenComparingInt(Candidate::startLine));

        try (BufferedWriter writer = Files.newBufferedWriter(output, StandardCharsets.UTF_8)) {
            for (Candidate candidate : candidates) {
                writer.write(mapper.writeValueAsString(candidate));
                writer.newLine();
            }
        }
        Path errorFile = output.resolveSibling("candidate-parse-errors.txt");
        Files.write(errorFile, errors, StandardCharsets.UTF_8);
        System.out.printf(Locale.ROOT,
                "projects=%d candidates=%d eligible=%d parseErrors=%d%n",
                projectDirectories.size(), candidates.size(),
                candidates.stream().filter(Candidate::eligible).count(), errors.size());
    }

    private static void scanProject(
            Path projectsRoot, Path project, String revision, JavaParser parser,
            List<Candidate> candidates, List<String> errors) throws Exception {
        String projectId = project.getFileName().toString();
        try (Stream<Path> paths = Files.walk(project)) {
            for (Path source : paths.filter(Files::isRegularFile)
                    .filter(path -> path.toString().endsWith(".java"))
                    .filter(path -> !hasExcludedSegment(project.relativize(path)))
                    .filter(path -> !isTestSource(project.relativize(path)))
                    .sorted().toList()) {
                var result = parser.parse(source);
                if (result.getResult().isEmpty()) {
                    errors.add(projectId + "\t" + project.relativize(source) + "\t" + result.getProblems());
                    continue;
                }
                CompilationUnit unit = result.getResult().get();
                String packageName = unit.getPackageDeclaration()
                        .map(value -> value.getNameAsString()).orElse("");
                for (MethodDeclaration method : unit.findAll(MethodDeclaration.class)) {
                    if (method.getBody().isEmpty()) continue;
                    candidates.add(toCandidate(
                            projectsRoot, project, projectId, revision,
                            source, unit, packageName, method));
                }
            }
        }
    }

    private static Candidate toCandidate(
            Path projectsRoot, Path project, String projectId, String revision,
            Path source, CompilationUnit unit, String packageName, MethodDeclaration method) throws Exception {
        int start = method.getBegin().map(position -> position.line).orElse(-1);
        int end = method.getEnd().map(position -> position.line).orElse(-1);
        int loc = start > 0 && end >= start ? end - start + 1 : 0;
        int complexity = cyclomaticComplexity(method);
        boolean explicitExceptionPath = !method.findAll(ThrowStmt.class).isEmpty();
        boolean branchRelevant = complexity >= 2 || explicitExceptionPath;
        String methodText = collectLocalDependencyText(method, unit).toLowerCase(Locale.ROOT);
        String runtimeDependencyText = collectRuntimeDependencyText(
                method, unit, packageName, methodText);
        List<String> reasons = new ArrayList<>();

        if (method.isPrivate() || method.isProtected()) reasons.add("NON_CALLABLE_VISIBILITY");
        if (hasPrivateParameterType(method, unit)) reasons.add("UNAVAILABLE_PARAMETER_TYPE");
        addTypeAccessibilityAndConstructionReasons(method, unit, reasons);
        if (method.isAbstract() || method.isNative()) reasons.add("NO_CONCRETE_BODY");
        if (method.getNameAsString().equals("main") && method.isStatic()) reasons.add("MAIN_ENTRY_POINT");
        if (method.getNameAsString().equalsIgnoreCase("test")) reasons.add("TEST_DRIVER_METHOD");
        if (isObjectBoilerplate(method)) reasons.add("OBJECT_BOILERPLATE");
        if (isTrivialAccessor(method)) reasons.add("TRIVIAL_ACCESSOR");
        if (!branchRelevant) reasons.add("NO_STRUCTURAL_DECISION");
        if (containsAny(runtimeDependencyText, EXTERNAL_MARKERS))
            reasons.add("EXTERNAL_SERVICE_OR_GUI");
        if (containsAny(runtimeDependencyText, RUNTIME_CONTAINER_MARKERS))
            reasons.add("UNAVAILABLE_RUNTIME_CONTAINER");
        if (containsAny(methodText, NONDETERMINISTIC_MARKERS)) reasons.add("NONDETERMINISTIC_INPUT");

        String simpleClass = enclosingClassName(method);
        if (simpleClass.toLowerCase(Locale.ROOT).endsWith("menu")) reasons.add("INTERACTIVE_MENU_CLASS");
        String focalClass = packageName.isBlank() ? simpleClass : packageName + "." + simpleClass;
        String signature = method.getNameAsString() + "(" + String.join(",",
                method.getParameters().stream().map(parameter -> {
                    String type = parameter.getType().asString();
                    return parameter.isVarArgs() ? type + "..." : type;
                }).toList()) + ")";
        String sourceFile = project.relativize(source).toString().replace('\\', '/');
        String sourceHash = sha256(source);
        String stratum = complexity == 2 ? "LOW" : complexity <= 5 ? "MEDIUM" : "HIGH";
        String stableKey = projectId + "|" + focalClass + "|" + signature + "|" + start;

        return new Candidate(
                stableKey, projectId, revision, ".", sourceFile,
                focalClass, signature, start, end, loc, complexity, stratum,
                method.isPublic() ? "public" : "package", explicitExceptionPath,
                reasons.isEmpty(), reasons, sourceHash);
    }

    private static int cyclomaticComplexity(MethodDeclaration method) {
        int complexity = 1;
        complexity += method.findAll(IfStmt.class).size();
        complexity += method.findAll(ForStmt.class).size();
        complexity += method.findAll(ForEachStmt.class).size();
        complexity += method.findAll(WhileStmt.class).size();
        complexity += method.findAll(DoStmt.class).size();
        complexity += method.findAll(CatchClause.class).size();
        complexity += method.findAll(ConditionalExpr.class).size();
        complexity += (int) method.findAll(SwitchEntry.class).stream()
                .filter(entry -> !entry.getLabels().isEmpty()).count();
        complexity += (int) method.findAll(BinaryExpr.class).stream()
                .filter(expression -> expression.getOperator() == BinaryExpr.Operator.AND
                        || expression.getOperator() == BinaryExpr.Operator.OR).count();
        return complexity;
    }

    private static boolean isObjectBoilerplate(MethodDeclaration method) {
        String name = method.getNameAsString();
        return name.equals("equals") || name.equals("hashCode") || name.equals("toString");
    }

    private static boolean isTrivialAccessor(MethodDeclaration method) {
        String name = method.getNameAsString();
        int statements = method.getBody().map(body -> body.getStatements().size()).orElse(0);
        boolean accessorName = name.startsWith("get") || name.startsWith("set")
                || name.startsWith("is") || name.startsWith("has");
        return accessorName && statements <= 1 && cyclomaticComplexity(method) == 1;
    }

    private static boolean hasPrivateParameterType(MethodDeclaration method, CompilationUnit unit) {
        Set<String> privateTypes = new HashSet<>();
        for (TypeDeclaration<?> type : unit.findAll(TypeDeclaration.class)) {
            if (type.isPrivate()) privateTypes.add(type.getNameAsString());
        }
        return method.getParameters().stream().anyMatch(parameter -> {
            String typeText = parameter.getType().asString();
            return privateTypes.stream().anyMatch(typeName ->
                    typeText.matches(".*\\b" + java.util.regex.Pattern.quote(typeName) + "\\b.*"));
        });
    }

    /**
     * Applies a conservative unit-testability gate. Tests are generated in the
     * production package, so package-private declarations remain callable, but
     * private/protected enclosing types and local/anonymous types are excluded.
     * For instance methods, every object required by a non-static inner-class
     * chain must expose at least one usable constructor.
     */
    private static void addTypeAccessibilityAndConstructionReasons(
            MethodDeclaration method, CompilationUnit unit, List<String> reasons) {
        Optional<TypeDeclaration<?>> declaringTypeResult = method.findAncestor(TypeDeclaration.class)
                .map(type -> (TypeDeclaration<?>) type);
        if (declaringTypeResult.isEmpty()) {
            addReason(reasons, "NO_ACCESSIBLE_CONSTRUCTION_PATH");
            return;
        }

        TypeDeclaration<?> declaringType = declaringTypeResult.get();
        if (isInsideAnonymousClass(method, declaringType)
                || isLocalType(declaringType)) {
            addReason(reasons, "LOCAL_OR_ANONYMOUS_TYPE");
        }

        List<TypeDeclaration<?>> chain = enclosingTypeChain(declaringType);
        if (chain.stream().anyMatch(type -> type.isPrivate() || type.isProtected()))
            addReason(reasons, "INACCESSIBLE_ENCLOSING_TYPE");

        if (method.isStatic()) return;
        if (!hasUsableInstancePath(declaringType, unit)) {
            addReason(reasons, "NO_ACCESSIBLE_CONSTRUCTION_PATH");
            return;
        }

        TypeDeclaration<?> current = declaringType;
        while (requiresEnclosingInstance(current)) {
            Optional<TypeDeclaration<?>> outerResult = current.findAncestor(TypeDeclaration.class)
                    .map(type -> (TypeDeclaration<?>) type);
            if (outerResult.isEmpty() || !hasUsableInstancePath(outerResult.get(), unit)) {
                addReason(reasons, "NON_STATIC_INNER_WITHOUT_OUTER_FACTORY");
                return;
            }
            current = outerResult.get();
        }
    }

    private static List<TypeDeclaration<?>> enclosingTypeChain(TypeDeclaration<?> declaringType) {
        List<TypeDeclaration<?>> result = new ArrayList<>();
        TypeDeclaration<?> current = declaringType;
        result.add(current);
        while (true) {
            Optional<TypeDeclaration<?>> outer = current.findAncestor(TypeDeclaration.class)
                    .map(type -> (TypeDeclaration<?>) type);
            if (outer.isEmpty()) return result;
            current = outer.get();
            result.add(current);
        }
    }

    private static boolean isInsideAnonymousClass(
            MethodDeclaration method, TypeDeclaration<?> declaringType) {
        Optional<Node> cursor = method.getParentNode();
        while (cursor.isPresent() && cursor.get() != declaringType) {
            if (cursor.get() instanceof ObjectCreationExpr creation
                    && creation.getAnonymousClassBody().isPresent()) return true;
            cursor = cursor.get().getParentNode();
        }
        return false;
    }

    private static boolean isLocalType(TypeDeclaration<?> type) {
        Optional<Node> parent = type.getParentNode();
        return parent.isPresent()
                && !(parent.get() instanceof CompilationUnit)
                && !(parent.get() instanceof TypeDeclaration<?>);
    }

    private static boolean requiresEnclosingInstance(TypeDeclaration<?> type) {
        Optional<TypeDeclaration<?>> outer = type.findAncestor(TypeDeclaration.class)
                .map(value -> (TypeDeclaration<?>) value);
        if (outer.isEmpty()) return false;
        if (type.isStatic()) return false;
        return !(outer.get() instanceof ClassOrInterfaceDeclaration declaration
                && declaration.isInterface());
    }

    private static boolean hasUsableInstancePath(
            TypeDeclaration<?> type, CompilationUnit unit) {
        if (!(type instanceof ClassOrInterfaceDeclaration declaration)) {
            // Enum constants provide instances without a constructor call.
            return type.isEnumDeclaration()
                    && !type.asEnumDeclaration().getEntries().isEmpty();
        }
        if (declaration.isInterface() || declaration.isAbstract()) return false;
        List<ConstructorDeclaration> constructors = declaration.getConstructors();
        if (constructors.isEmpty()) return true;
        return constructors.stream().anyMatch(constructor ->
                !constructor.isPrivate()
                        && !constructor.isProtected()
                        && !hasPrivateConstructorParameterType(constructor, unit));
    }

    private static boolean hasPrivateConstructorParameterType(
            ConstructorDeclaration constructor, CompilationUnit unit) {
        Set<String> privateTypes = new HashSet<>();
        for (TypeDeclaration<?> type : unit.findAll(TypeDeclaration.class)) {
            if (type.isPrivate()) privateTypes.add(type.getNameAsString());
        }
        return constructor.getParameters().stream().anyMatch(parameter -> {
            String typeText = parameter.getType().asString();
            return privateTypes.stream().anyMatch(typeName ->
                    typeText.matches(".*\\b" + java.util.regex.Pattern.quote(typeName) + "\\b.*"));
        });
    }

    private static void addReason(List<String> reasons, String reason) {
        if (!reasons.contains(reason)) reasons.add(reason);
    }

    private static String enclosingClassName(Node node) {
        List<String> names = new ArrayList<>();
        Optional<Node> cursor = Optional.of(node);
        while (cursor.isPresent()) {
            Node value = cursor.get();
            if (value instanceof TypeDeclaration<?> type) names.add(type.getNameAsString());
            cursor = value.getParentNode();
        }
        Collections.reverse(names);
        return String.join("$", names);
    }

    private static boolean containsAny(String text, List<String> markers) {
        return markers.stream().anyMatch(text::contains);
    }

    private static String collectLocalDependencyText(MethodDeclaration root, CompilationUnit unit) {
        String enclosingClass = enclosingClassName(root);
        Map<String, List<MethodDeclaration>> localMethods = new HashMap<>();
        for (MethodDeclaration candidate : unit.findAll(MethodDeclaration.class)) {
            if (!enclosingClassName(candidate).equals(enclosingClass)) continue;
            localMethods.computeIfAbsent(candidate.getNameAsString(), ignored -> new ArrayList<>()).add(candidate);
        }
        StringBuilder result = new StringBuilder();
        Deque<MethodDeclaration> pending = new ArrayDeque<>();
        Set<MethodDeclaration> visited = Collections.newSetFromMap(new IdentityHashMap<>());
        pending.add(root);
        while (!pending.isEmpty()) {
            MethodDeclaration current = pending.removeFirst();
            if (!visited.add(current)) continue;
            result.append('\n').append(current);
            for (MethodCallExpr call : current.findAll(MethodCallExpr.class)) {
                boolean localCall = call.getScope().isEmpty()
                        || call.getScope().map(scope -> scope.isThisExpr()).orElse(false);
                if (!localCall) continue;
                for (MethodDeclaration dependency : localMethods.getOrDefault(
                        call.getNameAsString(), List.of())) pending.addLast(dependency);
            }
        }
        return result.toString();
    }

    private static String collectRuntimeDependencyText(
            MethodDeclaration method, CompilationUnit unit, String packageName, String methodText) {
        StringBuilder result = new StringBuilder(methodText)
                .append('\n').append(packageName.toLowerCase(Locale.ROOT));
        unit.getImports().forEach(importDeclaration -> result.append('\n')
                .append(importDeclaration.toString().toLowerCase(Locale.ROOT)));
        method.findAncestor(TypeDeclaration.class).ifPresent(type -> {
            result.append('\n').append(type.getNameAsString().toLowerCase(Locale.ROOT));
            if (type instanceof ClassOrInterfaceDeclaration declaration) {
                declaration.getExtendedTypes().forEach(extended -> result.append('\n')
                        .append(extended.toString().toLowerCase(Locale.ROOT)));
                declaration.getImplementedTypes().forEach(implemented -> result.append('\n')
                        .append(implemented.toString().toLowerCase(Locale.ROOT)));
            }
        });
        return result.toString();
    }

    private static boolean isTestSource(Path relative) {
        String value = relative.toString().replace('\\', '/').toLowerCase(Locale.ROOT);
        return value.contains("/src/test/") || value.startsWith("src/test/")
                || value.contains("/test/") || value.startsWith("test/");
    }

    private static boolean hasExcludedSegment(Path relative) {
        for (Path segment : relative) if (EXCLUDED_DIRS.contains(segment.toString())) return true;
        return false;
    }

    private static int countDirectories(Path root) throws Exception {
        try (Stream<Path> paths = Files.list(root)) {
            return (int) paths.filter(Files::isDirectory).count();
        }
    }

    private static List<Path> listProjectDirectories(Path root) throws Exception {
        try (Stream<Path> paths = Files.list(root)) {
            return paths.filter(Files::isDirectory).sorted().toList();
        }
    }

    private static String sha256(Path path) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (var input = Files.newInputStream(path)) {
            byte[] buffer = new byte[8192];
            for (int read; (read = input.read(buffer)) >= 0;) digest.update(buffer, 0, read);
        }
        return HexFormat.of().formatHex(digest.digest());
    }

    private static Map<String, String> parseArgs(String[] args) {
        Map<String, String> values = new HashMap<>();
        for (int index = 0; index < args.length; index += 2) {
            if (index + 1 >= args.length) throw new IllegalArgumentException("Missing value for " + args[index]);
            values.put(args[index], args[index + 1]);
        }
        return values;
    }

    private static Path requiredPath(Map<String, String> options, String name) {
        String value = options.get(name);
        if (value == null || value.isBlank()) throw new IllegalArgumentException("Required: " + name);
        return Path.of(value).toAbsolutePath().normalize();
    }

    public record Candidate(
            String candidateKey,
            String projectId,
            String revision,
            String modulePath,
            String sourceFile,
            String focalClass,
            String focalMethodSignature,
            int startLine,
            int endLine,
            int loc,
            int cyclomaticComplexity,
            String complexityStratum,
            String visibility,
            boolean explicitExceptionPath,
            boolean eligible,
            List<String> exclusionReasons,
            String sourceSha256) { }
}
