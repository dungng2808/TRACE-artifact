package io.rq1.analyzer;

import com.github.javaparser.Range;
import com.github.javaparser.ast.Node;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.expr.Expression;
import com.github.javaparser.ast.stmt.*;

import java.util.*;

/**
 * Builds a deterministic, intraprocedural control-flow graph directly from a
 * JavaParser method AST. The graph is intentionally source-level: it models
 * explicit Java control constructs and conservative catch edges, not JVM
 * bytecode instructions or implicit runtime exceptions.
 */
public final class SourceControlFlowGraphBuilder {
    public static final String SEMANTICS = "SOURCE_LEVEL_INTRAPROCEDURAL_CFG_V1";

    private final List<MutableNode> nodes = new ArrayList<>();
    private final Set<Edge> edges = new LinkedHashSet<>();
    private int nextNodeNumber = 1;

    private SourceControlFlowGraphBuilder() { }

    public static ControlFlowGraph build(MethodDeclaration method) {
        var builder = new SourceControlFlowGraphBuilder();
        builder.nodes.add(new MutableNode("ENTRY", "ENTRY", -1, "UNKNOWN", "method entry"));
        builder.nodes.add(new MutableNode("EXIT", "EXIT", -1, "UNKNOWN", "method exit"));

        var rootContext = new FlowContext("EXIT", "EXIT", "EXIT", List.of());
        String bodyEntry = method.getBody()
                .map(body -> builder.buildBlock(body, "EXIT", rootContext))
                .orElse("EXIT");
        builder.addEdge("ENTRY", bodyEntry, "NORMAL", "entry");

        List<CfgNode> immutableNodes = builder.nodes.stream()
                .map(node -> new CfgNode(node.id, node.kind, node.line, node.sourceRange, node.statement))
                .sorted(Comparator.comparing(CfgNode::id, SourceControlFlowGraphBuilder::compareNodeIds))
                .toList();
        List<Edge> immutableEdges = builder.edges.stream()
                .sorted(Comparator.comparing(Edge::from, SourceControlFlowGraphBuilder::compareNodeIds)
                        .thenComparing(Edge::to, SourceControlFlowGraphBuilder::compareNodeIds)
                        .thenComparing(Edge::kind)
                        .thenComparing(Edge::label))
                .toList();
        return new ControlFlowGraph(
                "ENTRY", List.of("EXIT"), SEMANTICS,
                List.of(
                        "Explicit source-level control constructs are modeled.",
                        "Catch edges are conservative; implicit JVM exceptions are not resolved.",
                        "finally is modeled on normal try/catch completion, not every abrupt completion.",
                        "Short-circuit, ternary, switch-expression and label-specific subflows are not expanded."),
                immutableNodes, immutableEdges);
    }

    private String buildBlock(BlockStmt block, String next, FlowContext context) {
        String entry = next;
        List<Statement> statements = block.getStatements();
        for (int index = statements.size() - 1; index >= 0; index--)
            entry = buildStatement(statements.get(index), entry, context);
        return entry;
    }

    private String buildStatement(Statement statement, String next, FlowContext context) {
        if (statement.isBlockStmt())
            return buildBlock(statement.asBlockStmt(), next, context);

        if (statement.isIfStmt()) {
            IfStmt value = statement.asIfStmt();
            String decision = addNode("IF_CONDITION", value.getCondition(), value.getCondition().toString());
            String thenEntry = buildStatement(value.getThenStmt(), next, context);
            String elseEntry = value.getElseStmt()
                    .map(item -> buildStatement(item, next, context)).orElse(next);
            addEdge(decision, thenEntry, "TRUE", "true");
            addEdge(decision, elseEntry, "FALSE", "false");
            return decision;
        }

        if (statement.isWhileStmt()) {
            WhileStmt value = statement.asWhileStmt();
            String decision = addNode("WHILE_CONDITION", value.getCondition(), value.getCondition().toString());
            FlowContext loop = context.withLoop(next, decision);
            String bodyEntry = buildStatement(value.getBody(), decision, loop);
            addEdge(decision, bodyEntry, "TRUE", "true");
            addEdge(decision, next, "FALSE", "false");
            return decision;
        }

        if (statement.isDoStmt()) {
            DoStmt value = statement.asDoStmt();
            String decision = addNode("DO_WHILE_CONDITION", value.getCondition(), value.getCondition().toString());
            FlowContext loop = context.withLoop(next, decision);
            String bodyEntry = buildStatement(value.getBody(), decision, loop);
            addEdge(decision, bodyEntry, "LOOP_BACK", "true");
            addEdge(decision, next, "FALSE", "false");
            return bodyEntry;
        }

        if (statement.isForStmt()) {
            ForStmt value = statement.asForStmt();
            String conditionText = value.getCompare().map(Object::toString).orElse("true");
            Node conditionLocation = value.getCompare().map(Node.class::cast).orElse(value);
            String decision = addNode("FOR_CONDITION", conditionLocation, conditionText);
            String updateEntry = buildExpressions(value.getUpdate(), decision, "FOR_UPDATE");
            FlowContext loop = context.withLoop(next, updateEntry);
            String bodyEntry = buildStatement(value.getBody(), updateEntry, loop);
            addEdge(decision, bodyEntry, "TRUE", "true");
            if (value.getCompare().isPresent()) addEdge(decision, next, "FALSE", "false");
            return buildExpressions(value.getInitialization(), decision, "FOR_INIT");
        }

        if (statement.isForEachStmt()) {
            ForEachStmt value = statement.asForEachStmt();
            String label = value.getVariable() + " : " + value.getIterable();
            String decision = addNode("FOR_EACH_CONDITION", value, label);
            FlowContext loop = context.withLoop(next, decision);
            String bodyEntry = buildStatement(value.getBody(), decision, loop);
            addEdge(decision, bodyEntry, "TRUE", "has-next");
            addEdge(decision, next, "FALSE", "exhausted");
            return decision;
        }

        if (statement.isSwitchStmt())
            return buildSwitch(statement.asSwitchStmt(), next, context);

        if (statement.isTryStmt())
            return buildTry(statement.asTryStmt(), next, context);

        if (statement.isSynchronizedStmt()) {
            SynchronizedStmt value = statement.asSynchronizedStmt();
            String lock = addNode("SYNCHRONIZED", value.getExpression(), value.getExpression().toString());
            String body = buildBlock(value.getBody(), next, context);
            addEdge(lock, body, "NORMAL", "acquire");
            return lock;
        }

        if (statement.isLabeledStmt())
            return buildStatement(statement.asLabeledStmt().getStatement(), next, context);

        if (statement.isReturnStmt()) {
            String node = addNode("RETURN", statement, compact(statement));
            addEdge(node, context.returnTarget, "RETURN", "return");
            return node;
        }

        if (statement.isThrowStmt()) {
            String node = addNode("THROW", statement, compact(statement));
            if (context.exceptionTargets.isEmpty())
                addEdge(node, context.throwTarget, "THROW", "uncaught");
            else
                for (ExceptionTarget target : context.exceptionTargets)
                    addEdge(node, target.nodeId, "EXCEPTION", target.label);
            return node;
        }

        if (statement.isBreakStmt()) {
            String node = addNode("BREAK", statement, compact(statement));
            addEdge(node, context.breakTarget, "BREAK", statement.asBreakStmt().getLabel()
                    .map(Object::toString).orElse("break"));
            return node;
        }

        if (statement.isContinueStmt()) {
            String node = addNode("CONTINUE", statement, compact(statement));
            addEdge(node, context.continueTarget, "CONTINUE", statement.asContinueStmt().getLabel()
                    .map(Object::toString).orElse("continue"));
            return node;
        }

        String kind = statement.isExpressionStmt() ? "STATEMENT"
                : statement.isExplicitConstructorInvocationStmt() ? "CONSTRUCTOR_INVOCATION"
                : statement.isAssertStmt() ? "ASSERT"
                : statement.isYieldStmt() ? "YIELD"
                : statement.isEmptyStmt() ? "EMPTY"
                : "STATEMENT";
        String node = addNode(kind, statement, compact(statement));
        addEdge(node, next, "NORMAL", "next");
        return node;
    }

    private String buildSwitch(SwitchStmt statement, String next, FlowContext context) {
        String decision = addNode("SWITCH_SELECTOR", statement.getSelector(), statement.getSelector().toString());
        String followingEntry = next;
        boolean hasDefault = false;
        List<SwitchEntry> entries = statement.getEntries();
        List<SwitchArm> arms = new ArrayList<>();
        FlowContext switchContext = context.withBreak(next);
        for (int index = entries.size() - 1; index >= 0; index--) {
            SwitchEntry entry = entries.get(index);
            String entryNext = entry.getType() == SwitchEntry.Type.STATEMENT_GROUP ? followingEntry : next;
            String armEntry = entryNext;
            List<Statement> armStatements = entry.getStatements();
            for (int statementIndex = armStatements.size() - 1; statementIndex >= 0; statementIndex--)
                armEntry = buildStatement(armStatements.get(statementIndex), armEntry, switchContext);
            if (armStatements.isEmpty()) armEntry = entryNext;
            if (entry.getLabels().isEmpty()) {
                hasDefault = true;
                arms.add(new SwitchArm(armEntry, "default"));
            } else {
                for (Expression label : entry.getLabels())
                    arms.add(new SwitchArm(armEntry, label.toString()));
            }
            followingEntry = armEntry;
        }
        arms.stream().sorted(Comparator.comparing(SwitchArm::label).thenComparing(SwitchArm::entry))
                .forEach(arm -> addEdge(decision, arm.entry, "CASE", arm.label));
        if (!hasDefault) addEdge(decision, next, "CASE", "no-match");
        return decision;
    }

    private String buildTry(TryStmt statement, String next, FlowContext context) {
        String completion = statement.getFinallyBlock()
                .map(block -> buildBlock(block, next, context)).orElse(next);
        List<ExceptionTarget> handlers = new ArrayList<>();
        for (CatchClause clause : statement.getCatchClauses()) {
            String handler = buildBlock(clause.getBody(), completion, context);
            if (clause.getBody().getStatements().isEmpty()) {
                handler = addNode("CATCH", clause, "catch (" + clause.getParameter() + ")");
                addEdge(handler, completion, "NORMAL", "handled");
            }
            handlers.add(new ExceptionTarget(handler, clause.getParameter().getTypeAsString()));
        }
        FlowContext tryContext = context.withExceptionTargets(handlers);
        String tryBody = buildBlock(statement.getTryBlock(), completion, tryContext);
        String tryNode = addNode("TRY", statement, statement.getResources().isEmpty()
                ? "try" : "try (" + statement.getResources() + ")");
        addEdge(tryNode, tryBody, "NORMAL", "try");
        for (ExceptionTarget handler : handlers)
            addEdge(tryNode, handler.nodeId, "EXCEPTION", handler.label);
        return tryNode;
    }

    private String buildExpressions(List<Expression> expressions, String next, String kind) {
        String entry = next;
        for (int index = expressions.size() - 1; index >= 0; index--) {
            Expression expression = expressions.get(index);
            String node = addNode(kind, expression, expression.toString());
            addEdge(node, entry, "NORMAL", "next");
            entry = node;
        }
        return entry;
    }

    private String addNode(String kind, Node source, String statement) {
        String id = String.format(Locale.ROOT, "N%03d", nextNodeNumber++);
        nodes.add(new MutableNode(id, kind,
                source.getBegin().map(position -> position.line).orElse(-1),
                source.getRange().map(SourceControlFlowGraphBuilder::formatRange).orElse("UNKNOWN"),
                statement.replaceAll("\\s+", " ").trim()));
        return id;
    }

    private void addEdge(String from, String to, String kind, String label) {
        edges.add(new Edge(from, to, kind, label));
    }

    private static String compact(Node node) {
        return node.toString().replaceAll("\\s+", " ").trim();
    }

    private static String formatRange(Range range) {
        return range.begin.line + ":" + range.begin.column + "-"
               + range.end.line + ":" + range.end.column;
    }

    private static int compareNodeIds(String left, String right) {
        if (left.equals(right)) return 0;
        if (left.equals("ENTRY")) return -1;
        if (right.equals("ENTRY")) return 1;
        if (left.equals("EXIT")) return 1;
        if (right.equals("EXIT")) return -1;
        return left.compareTo(right);
    }

    public record ControlFlowGraph(
            String entryNodeId,
            List<String> exitNodeIds,
            String semantics,
            List<String> limitations,
            List<CfgNode> nodes,
            List<Edge> edges) { }

    public record CfgNode(
            String id, String kind, int line, String sourceRange, String statement) { }

    public record Edge(String from, String to, String kind, String label) { }

    private record MutableNode(
            String id, String kind, int line, String sourceRange, String statement) { }

    private record ExceptionTarget(String nodeId, String label) { }

    private record SwitchArm(String entry, String label) { }

    private record FlowContext(
            String breakTarget,
            String continueTarget,
            String returnTarget,
            String throwTarget,
            List<ExceptionTarget> exceptionTargets) {
        private FlowContext(String breakTarget, String continueTarget, String returnTarget,
                            List<ExceptionTarget> exceptionTargets) {
            this(breakTarget, continueTarget, returnTarget, "EXIT", exceptionTargets);
        }

        private FlowContext withLoop(String breakValue, String continueValue) {
            return new FlowContext(breakValue, continueValue, returnTarget, throwTarget, exceptionTargets);
        }

        private FlowContext withBreak(String breakValue) {
            return new FlowContext(breakValue, continueTarget, returnTarget, throwTarget, exceptionTargets);
        }

        private FlowContext withExceptionTargets(List<ExceptionTarget> targets) {
            return new FlowContext(breakTarget, continueTarget, returnTarget, throwTarget, List.copyOf(targets));
        }
    }
}
