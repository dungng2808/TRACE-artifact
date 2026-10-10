package io.rq1.analyzer;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;
import com.github.javaparser.JavaParser;
import com.github.javaparser.ParserConfiguration;
import com.github.javaparser.Position;
import com.github.javaparser.Range;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.Node;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.body.TypeDeclaration;
import com.github.javaparser.ast.expr.ConditionalExpr;
import com.github.javaparser.ast.expr.BinaryExpr;
import com.github.javaparser.ast.expr.Expression;
import com.github.javaparser.ast.expr.SwitchExpr;
import com.github.javaparser.ast.stmt.*;

import java.io.BufferedWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.MessageDigest;
import java.util.*;

public final class TargetExtractorMain {
    private static final String VERSION = "target-analyzer-v2-cfg";
    private static final String OFFICIAL_SEMANTICS = "FULL_BRANCH_SITE_COVERAGE";

    private TargetExtractorMain() { }

    public static void main(String[] args) throws Exception {
        Map<String, String> options = parseArgs(args);
        Path projectsRoot = requiredPath(options, "--projects-root");
        Path subjectsPath = requiredPath(options, "--subjects");
        Path output = requiredPath(options, "--output");
        Path errorsPath = output.resolveSibling(output.getFileName() + ".errors.txt");
        Files.createDirectories(output.toAbsolutePath().getParent());

        ObjectMapper mapper = new ObjectMapper().disable(SerializationFeature.FAIL_ON_EMPTY_BEANS);
        JavaParser parser = new JavaParser(new ParserConfiguration()
                .setLanguageLevel(ParserConfiguration.LanguageLevel.BLEEDING_EDGE));
        List<Inventory> inventories = new ArrayList<>();
        List<String> errors = new ArrayList<>();
        for (String line : Files.readAllLines(subjectsPath, StandardCharsets.UTF_8)) {
            if (line.isBlank()) continue;
            JsonNode subject = mapper.readTree(line);
            try {
                inventories.add(extract(subject, projectsRoot, parser));
            } catch (Exception exception) {
                errors.add(subject.path("subjectId").asText("UNKNOWN") + "\t" + exception.getMessage());
            }
        }
        inventories.sort(Comparator.comparing(Inventory::subjectId));
        try (BufferedWriter writer = Files.newBufferedWriter(output, StandardCharsets.UTF_8)) {
            for (Inventory inventory : inventories) {
                writer.write(mapper.writeValueAsString(inventory));
                writer.newLine();
            }
        }
        Files.write(errorsPath, errors, StandardCharsets.UTF_8);
        int officialTargets = inventories.stream().mapToInt(inventory ->
                (int) inventory.targets().stream().filter(target -> target.measurementRole().equals("OFFICIAL")).count()).sum();
        int cfgNodes = inventories.stream().mapToInt(inventory -> inventory.controlFlowGraph().nodes().size()).sum();
        int cfgEdges = inventories.stream().mapToInt(inventory -> inventory.controlFlowGraph().edges().size()).sum();
        System.out.printf(Locale.ROOT,
                "subjects=%d officialBranchSites=%d cfgNodes=%d cfgEdges=%d errors=%d%n",
                inventories.size(), officialTargets, cfgNodes, cfgEdges, errors.size());
        if (!errors.isEmpty()) System.exit(2);
    }

    private static Inventory extract(JsonNode subject, Path projectsRoot, JavaParser parser) throws Exception {
        String projectId = required(subject, "projectId");
        String sourceFile = required(subject, "sourceFile");
        Path source = projectsRoot.resolve(projectId).resolve(sourceFile).normalize();
        if (!source.startsWith(projectsRoot) || !Files.isRegularFile(source))
            throw new IllegalArgumentException("Source file does not resolve inside projects root: " + sourceFile);
        String expectedHash = required(subject, "sourceSha256");
        String actualHash = sha256(source);
        if (!actualHash.equalsIgnoreCase(expectedHash))
            throw new IllegalStateException("Source SHA-256 mismatch");

        CompilationUnit unit = parser.parse(source).getResult()
                .orElseThrow(() -> new IllegalStateException("Java parse failed"));
        String focalClass = required(subject, "focalClass");
        String signature = required(subject, "focalMethodSignature");
        int startLine = subject.path("startLine").asInt(-1);
        MethodDeclaration method = unit.findAll(MethodDeclaration.class).stream()
                .filter(candidate -> fullClassName(unit, candidate).equals(focalClass))
                .filter(candidate -> signature(candidate).equals(signature))
                .filter(candidate -> candidate.getBegin().map(position -> position.line).orElse(-1) == startLine)
                .findFirst()
                .orElseThrow(() -> new IllegalStateException("Exact focal method not found"));

        List<RawTarget> rawTargets = new ArrayList<>();
        addBranchSites(method, rawTargets);
        addReturnPaths(method, rawTargets);
        addExceptionPaths(method, rawTargets);
        rawTargets.sort(Comparator.comparingInt(RawTarget::line)
                .thenComparing(RawTarget::sourceRange)
                .thenComparing(RawTarget::type)
                .thenComparing(target -> Optional.ofNullable(target.statement()).orElse("")));

        List<Target> targets = new ArrayList<>();
        for (int index = 0; index < rawTargets.size(); index++) {
            RawTarget raw = rawTargets.get(index);
            targets.add(new Target(
                    String.format(Locale.ROOT, "%s-T%03d", required(subject, "subjectId"), index + 1),
                    raw.type(), raw.measurementRole(), raw.measurementSemantics(), raw.line(),
                    raw.sourceRange(), raw.predicate(), raw.statement(), raw.exceptionType()));
        }
        SourceControlFlowGraphBuilder.ControlFlowGraph controlFlowGraph =
                SourceControlFlowGraphBuilder.build(method);
        return new Inventory(
                required(subject, "subjectId"), required(subject, "split"), projectId,
                focalClass, signature, sourceFile, actualHash, VERSION, OFFICIAL_SEMANTICS,
                targets, controlFlowGraph);
    }

    private static void addBranchSites(MethodDeclaration method, List<RawTarget> targets) {
        List<Expression> structuralPredicates = new ArrayList<>();
        for (IfStmt statement : method.findAll(IfStmt.class))
            addBranch(targets, statement.getCondition(), branchCount(statement.getCondition()), structuralPredicates);
        for (WhileStmt statement : method.findAll(WhileStmt.class))
            addBranch(targets, statement.getCondition(), branchCount(statement.getCondition()), structuralPredicates);
        for (DoStmt statement : method.findAll(DoStmt.class))
            addBranch(targets, statement.getCondition(), branchCount(statement.getCondition()), structuralPredicates);
        for (ForStmt statement : method.findAll(ForStmt.class))
            statement.getCompare().ifPresent(condition ->
                    addBranch(targets, condition, branchCount(condition), structuralPredicates));
        for (ForEachStmt statement : method.findAll(ForEachStmt.class))
            addBranch(targets, statement, 2, "forEach(" + statement.getVariable() + " : " + statement.getIterable() + ")");
        for (ConditionalExpr expression : method.findAll(ConditionalExpr.class))
            addBranch(targets, expression.getCondition(), branchCount(expression.getCondition()), structuralPredicates);
        for (SwitchStmt statement : method.findAll(SwitchStmt.class)) {
            int branches = (int) statement.getEntries().stream().filter(entry -> !entry.getLabels().isEmpty()).count();
            boolean hasDefault = statement.getEntries().stream().anyMatch(entry -> entry.getLabels().isEmpty());
            if (!hasDefault) branches++;
            addBranch(targets, statement.getSelector(), Math.max(2, branches), structuralPredicates);
        }
        for (SwitchExpr expression : method.findAll(SwitchExpr.class)) {
            int branches = (int) expression.getEntries().stream().filter(entry -> !entry.getLabels().isEmpty()).count();
            boolean hasDefault = expression.getEntries().stream().anyMatch(entry -> entry.getLabels().isEmpty());
            if (!hasDefault) branches++;
            addBranch(targets, expression.getSelector(), Math.max(2, branches), structuralPredicates);
        }
        for (BinaryExpr expression : method.findAll(BinaryExpr.class)) {
            if (expression.getOperator() != BinaryExpr.Operator.AND
                    && expression.getOperator() != BinaryExpr.Operator.OR) continue;
            if (structuralPredicates.stream().anyMatch(root -> root == expression || root.isAncestorOf(expression)))
                continue;
            boolean nestedLogical = expression.getParentNode()
                    .filter(BinaryExpr.class::isInstance)
                    .map(BinaryExpr.class::cast)
                    .map(parent -> parent.getOperator() == BinaryExpr.Operator.AND
                            || parent.getOperator() == BinaryExpr.Operator.OR)
                    .orElse(false);
            if (!nestedLogical)
                addBranch(targets, expression, branchCount(expression), structuralPredicates);
        }
    }

    private static int branchCount(Expression expression) {
        int logicalOperators = (int) expression.findAll(BinaryExpr.class).stream()
                .filter(binary -> binary.getOperator() == BinaryExpr.Operator.AND
                        || binary.getOperator() == BinaryExpr.Operator.OR).count();
        return 2 * (logicalOperators + 1);
    }

    private static void addBranch(
            List<RawTarget> targets,
            Expression expression,
            int expectedBranches,
            List<Expression> structuralPredicates) {
        structuralPredicates.add(expression);
        addBranch(targets, expression, expectedBranches, expression.toString());
    }

    private static void addBranch(List<RawTarget> targets, Node location, int expectedBranches, String predicate) {
        targets.add(new RawTarget(
                "BRANCH_SITE", "OFFICIAL", "ALL_JACOCO_BRANCHES_AT_SOURCE_LINE",
                line(location), range(location), predicate, null, null));
    }

    private static void addReturnPaths(MethodDeclaration method, List<RawTarget> targets) {
        for (ReturnStmt statement : method.findAll(ReturnStmt.class)) {
            targets.add(new RawTarget(
                    "RETURN_PATH", "DIAGNOSTIC", "SOURCE_MAPPED_PATH_OBLIGATION",
                    line(statement), range(statement), null, statement.toString(), null));
        }
        if (method.getType().isVoidType() && mayCompleteNormally(method)) {
            targets.add(new RawTarget(
                    "RETURN_PATH", "DIAGNOSTIC", "IMPLICIT_NORMAL_COMPLETION",
                    method.getEnd().map(position -> position.line).orElse(line(method)), range(method),
                    null, "implicit void return", null));
        }
    }

    private static boolean mayCompleteNormally(MethodDeclaration method) {
        if (method.getBody().isEmpty() || method.getBody().get().getStatements().isEmpty()) return true;
        Statement last = method.getBody().get().getStatement(method.getBody().get().getStatements().size() - 1);
        return !(last.isReturnStmt() || last.isThrowStmt());
    }

    private static void addExceptionPaths(MethodDeclaration method, List<RawTarget> targets) {
        for (CatchClause clause : method.findAll(CatchClause.class)) {
            targets.add(new RawTarget(
                    "EXPLICIT_EXCEPTION_PATH", "DIAGNOSTIC", "SOURCE_MAPPED_CATCH_HANDLER",
                    line(clause), range(clause), null,
                    "catch (" + clause.getParameter() + ")", clause.getParameter().getTypeAsString()));
        }
        for (ThrowStmt statement : method.findAll(ThrowStmt.class)) {
            String exceptionType = statement.getExpression().isObjectCreationExpr()
                    ? statement.getExpression().asObjectCreationExpr().getTypeAsString()
                    : "EXPRESSION_DERIVED";
            targets.add(new RawTarget(
                    "EXPLICIT_EXCEPTION_PATH", "DIAGNOSTIC", "SOURCE_MAPPED_THROW_STATEMENT",
                    line(statement), range(statement), null, statement.toString(), exceptionType));
        }
    }

    private static String fullClassName(CompilationUnit unit, Node node) {
        List<String> names = new ArrayList<>();
        Optional<Node> cursor = Optional.of(node);
        while (cursor.isPresent()) {
            Node value = cursor.get();
            if (value instanceof TypeDeclaration<?> type) names.add(type.getNameAsString());
            cursor = value.getParentNode();
        }
        Collections.reverse(names);
        String local = String.join("$", names);
        String packageName = unit.getPackageDeclaration().map(value -> value.getNameAsString()).orElse("");
        return packageName.isBlank() ? local : packageName + "." + local;
    }

    private static String signature(MethodDeclaration method) {
        return method.getNameAsString() + "(" + String.join(",", method.getParameters().stream().map(parameter -> {
            String type = parameter.getType().asString();
            return parameter.isVarArgs() ? type + "..." : type;
        }).toList()) + ")";
    }

    private static int line(Node node) {
        return node.getBegin().map(position -> position.line).orElse(-1);
    }

    private static String range(Node node) {
        return node.getRange().map(TargetExtractorMain::formatRange).orElse("UNKNOWN");
    }

    private static String formatRange(Range range) {
        Position begin = range.begin;
        Position end = range.end;
        return begin.line + ":" + begin.column + "-" + end.line + ":" + end.column;
    }

    private static String required(JsonNode node, String name) {
        String value = node.path(name).asText();
        if (value.isBlank()) throw new IllegalArgumentException("Missing subject field: " + name);
        return value;
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

    private static String sha256(Path path) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (var input = Files.newInputStream(path)) {
            byte[] buffer = new byte[8192];
            for (int read; (read = input.read(buffer)) >= 0;) digest.update(buffer, 0, read);
        }
        return HexFormat.of().formatHex(digest.digest());
    }

    public record Inventory(
            String subjectId, String split, String projectId, String focalClass,
            String focalMethodSignature, String sourceFile, String sourceSha256,
            String analyzerVersion, String officialTargetSemantics, List<Target> targets,
            SourceControlFlowGraphBuilder.ControlFlowGraph controlFlowGraph) { }

    public record Target(
            String targetId, String type, String measurementRole, String measurementSemantics,
            int line, String sourceRange, String predicate, String statement,
            String exceptionType) { }

    private record RawTarget(
            String type, String measurementRole, String measurementSemantics,
            int line, String sourceRange, String predicate, String statement,
            String exceptionType) { }
}
