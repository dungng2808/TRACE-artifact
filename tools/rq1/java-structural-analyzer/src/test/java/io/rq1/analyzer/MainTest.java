package io.rq1.analyzer;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

class MainTest {
    @TempDir
    Path temporaryDirectory;

    @Test
    void targetExtractorBuildsDeterministicConnectedSourceCfg() throws Exception {
        Path projects = Files.createDirectories(temporaryDirectory.resolve("cfg-projects"));
        Path project = Files.createDirectories(projects.resolve("fixture"));
        Path source = project.resolve("src/main/java/sample/Flow.java");
        Files.createDirectories(source.getParent());
        Files.writeString(source, """
                package sample;

                public class Flow {
                    public int classify(int x) {
                        int total = 0;
                        while (x > 0) {
                            if (x == 2) { x--; continue; }
                            if (x == 1) break;
                            total += x--;
                        }
                        switch (total) {
                            case 0: total = 1; break;
                            default: total = 2;
                        }
                        try {
                            if (x < 0) throw new IllegalArgumentException("negative");
                        } catch (IllegalArgumentException exception) {
                            return -1;
                        }
                        return total;
                    }
                }
                """);

        ObjectMapper mapper = new ObjectMapper();
        var subject = mapper.createObjectNode();
        subject.put("subjectId", "FIX-CFG-01");
        subject.put("split", "SMOKE");
        subject.put("projectId", "fixture");
        subject.put("sourceFile", "src/main/java/sample/Flow.java");
        subject.put("focalClass", "sample.Flow");
        subject.put("focalMethodSignature", "classify(int)");
        subject.put("startLine", 4);
        subject.put("sourceSha256", HexFormat.of().formatHex(
                MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(source))));
        Path subjects = temporaryDirectory.resolve("cfg-subjects.jsonl");
        Files.writeString(subjects, mapper.writeValueAsString(subject) + System.lineSeparator());
        Path first = temporaryDirectory.resolve("cfg-first.jsonl");
        Path second = temporaryDirectory.resolve("cfg-second.jsonl");

        TargetExtractorMain.main(new String[] {
                "--projects-root", projects.toString(), "--subjects", subjects.toString(),
                "--output", first.toString()
        });
        TargetExtractorMain.main(new String[] {
                "--projects-root", projects.toString(), "--subjects", subjects.toString(),
                "--output", second.toString()
        });

        assertEquals(Files.readString(first), Files.readString(second));
        JsonNode inventory = mapper.readTree(Files.readString(first).trim());
        assertEquals("target-analyzer-v2-cfg", inventory.path("analyzerVersion").asText());
        JsonNode graph = inventory.path("controlFlowGraph");
        assertEquals("SOURCE_LEVEL_INTRAPROCEDURAL_CFG_V1", graph.path("semantics").asText());
        assertEquals("ENTRY", graph.path("entryNodeId").asText());
        assertTrue(toTextValues(graph.path("exitNodeIds")).contains("EXIT"), graph.toPrettyString());
        var nodeIds = toTextValues(graph.path("nodes"), "id");
        assertEquals(nodeIds.size(), nodeIds.stream().distinct().count());
        var edgeKinds = toTextValues(graph.path("edges"), "kind");
        assertTrue(edgeKinds.containsAll(List.of(
                "TRUE", "FALSE", "CONTINUE", "BREAK", "CASE", "EXCEPTION", "RETURN")),
                graph.toPrettyString());
        graph.path("edges").forEach(edge -> {
            assertTrue(nodeIds.contains(edge.path("from").asText()), edge.toPrettyString());
            assertTrue(nodeIds.contains(edge.path("to").asText()), edge.toPrettyString());
        });
    }

    @Test
    void testabilityGateRejectsInaccessibleConstructionAndRuntimeSubjects() throws Exception {
        Path projects = Files.createDirectories(temporaryDirectory.resolve("projects"));
        Path project = Files.createDirectories(projects.resolve("fixture"));
        Path source = project.resolve("src/main/java/sample/Outer.java");
        Files.createDirectories(source.getParent());
        Files.writeString(source, """
                package sample;

                public class Outer {
                    private Outer() { }

                    private class Hidden {
                        public int choose(int value) { return value < 0 ? -1 : 1; }
                    }

                    public class MemberWithoutOuterFactory {
                        public int choose(int value) { return value < 0 ? -1 : 1; }
                    }

                    public static class ClosedConstruction {
                        private ClosedConstruction() { }
                        public int choose(int value) { return value < 0 ? -1 : 1; }
                    }

                    public static class PluginBound {
                        public int choose(SelectionEvent event) { return event == null ? 0 : 1; }
                    }

                    public static class Pure {
                        public int choose(int value) { return value < 0 ? -1 : 1; }
                    }

                    public void declareLocal() {
                        class Local {
                            public int choose(int value) { return value < 0 ? -1 : 1; }
                        }
                    }
                }
                """);

        Path output = temporaryDirectory.resolve("candidates.jsonl");
        Main.main(new String[] {
                "--projects-root", projects.toString(),
                "--output", output.toString(),
                "--revision", "fixture-revision"
        });

        List<JsonNode> candidates = new ArrayList<>();
        ObjectMapper mapper = new ObjectMapper();
        for (String line : Files.readAllLines(output)) {
            if (!line.isBlank()) candidates.add(mapper.readTree(line));
        }

        assertExcluded(candidates, "Outer$Hidden", "choose(int)",
                "INACCESSIBLE_ENCLOSING_TYPE");
        assertExcluded(candidates, "Outer$MemberWithoutOuterFactory", "choose(int)",
                "NON_STATIC_INNER_WITHOUT_OUTER_FACTORY");
        assertExcluded(candidates, "Outer$ClosedConstruction", "choose(int)",
                "NO_ACCESSIBLE_CONSTRUCTION_PATH");
        assertExcluded(candidates, "Outer$PluginBound", "choose(SelectionEvent)",
                "UNAVAILABLE_RUNTIME_CONTAINER");
        assertExcluded(candidates, "Outer$Local", "choose(int)",
                "LOCAL_OR_ANONYMOUS_TYPE");

        JsonNode pure = find(candidates, "Outer$Pure", "choose(int)");
        assertTrue(pure.path("eligible").asBoolean(), pure.toPrettyString());
        assertTrue(pure.path("exclusionReasons").isEmpty(), pure.toPrettyString());
    }

    @Test
    void contextExtractorRecordsEveryFocalMethodOverload() throws Exception {
        Path projects = Files.createDirectories(temporaryDirectory.resolve("overload-projects"));
        Path project = Files.createDirectories(projects.resolve("fixture"));
        Path source = project.resolve("src/main/java/sample/Overloaded.java");
        Files.createDirectories(source.getParent());
        Files.writeString(source, """
                package sample;

                public class Overloaded {
                    public String choose(String value) { return new Collaborator(value).normalize(); }
                    public String choose(Integer value) { return String.valueOf(value); }
                }

                class Collaborator {
                    Collaborator(String value) { }
                    String normalize() { return "normalized"; }
                    private void hidden() { }
                }
                """);

        ObjectMapper mapper = new ObjectMapper();
        var subject = mapper.createObjectNode();
        subject.put("subjectId", "FIX-M01");
        subject.put("split", "SMOKE");
        subject.put("projectId", "fixture");
        subject.put("sourceFile", "src/main/java/sample/Overloaded.java");
        subject.put("focalClass", "sample.Overloaded");
        subject.put("focalMethodSignature", "choose(String)");
        subject.put("startLine", 4);
        subject.put("sourceSha256", HexFormat.of().formatHex(
                MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(source))));
        Path subjects = temporaryDirectory.resolve("subjects.jsonl");
        Files.writeString(subjects, mapper.writeValueAsString(subject) + System.lineSeparator());
        Path output = temporaryDirectory.resolve("contexts.jsonl");

        ContextExtractorMain.main(new String[] {
                "--projects-root", projects.toString(),
                "--subjects", subjects.toString(),
                "--output", output.toString()
        });

        JsonNode context = mapper.readTree(Files.readString(output).trim());
        List<String> overloads = toTextValues(context.path("focalMethodOverloadSignatures"));
        assertTrue(overloads.contains("choose(String)"), context.toPrettyString());
        assertTrue(overloads.contains("choose(Integer)"), context.toPrettyString());
        assertTrue(context.path("extractorVersion").asText().equals("context-extractor-v4"),
                context.toPrettyString());
        JsonNode collaborator = context.path("relatedTypeApis").get(0);
        assertTrue(collaborator.path("className").asText().equals("sample.Collaborator"),
                context.toPrettyString());
        assertTrue(toTextValues(collaborator.path("accessibleConstructors")).stream()
                        .anyMatch(value -> value.contains("Collaborator(String")),
                context.toPrettyString());
        assertTrue(toTextValues(collaborator.path("accessibleConstructorSources")).stream()
                        .anyMatch(value -> value.contains("Collaborator(String value)")),
                context.toPrettyString());
        assertTrue(toTextValues(collaborator.path("accessibleMethods")).stream()
                        .anyMatch(value -> value.contains("normalize()")),
                context.toPrettyString());
        assertFalse(toTextValues(collaborator.path("accessibleMethods")).stream()
                        .anyMatch(value -> value.contains("hidden()")),
                context.toPrettyString());
    }

    @Test
    void contextExtractorUsesExplicitImportToResolveAmbiguousGenericReceiver() throws Exception {
        Path projects = Files.createDirectories(temporaryDirectory.resolve("generic-projects"));
        Path project = Files.createDirectories(projects.resolve("fixture"));
        Path domain = project.resolve("src/main/java/domain/EventWrapper.java");
        Path generated = project.resolve("src/main/java/generated/EventWrapper.java");
        Path focal = project.resolve("src/main/java/sample/Encoder.java");
        Files.createDirectories(domain.getParent());
        Files.createDirectories(generated.getParent());
        Files.createDirectories(focal.getParent());
        Files.writeString(domain, """
                package domain;
                public class EventWrapper<T> {
                    private final T event;
                    public EventWrapper() { this(null); }
                    public EventWrapper(T event) { this.event = event; }
                    public T getEvent() { return event; }
                }
                """);
        Files.writeString(generated, """
                package generated;
                public class EventWrapper { public Object getEvent() { return new Object(); } }
                """);
        Files.writeString(focal, """
                package sample;
                import domain.EventWrapper;
                public class Encoder {
                    public Object convert(EventWrapper<String> wrapper) {
                        return wrapper.getEvent();
                    }
                }
                """);

        ObjectMapper mapper = new ObjectMapper();
        var subject = mapper.createObjectNode();
        subject.put("subjectId", "FIX-M02");
        subject.put("split", "SMOKE");
        subject.put("projectId", "fixture");
        subject.put("sourceFile", "src/main/java/sample/Encoder.java");
        subject.put("focalClass", "sample.Encoder");
        subject.put("focalMethodSignature", "convert(EventWrapper<String>)");
        subject.put("startLine", 4);
        subject.put("sourceSha256", HexFormat.of().formatHex(
                MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(focal))));
        Path subjects = temporaryDirectory.resolve("generic-subjects.jsonl");
        Files.writeString(subjects, mapper.writeValueAsString(subject) + System.lineSeparator());
        Path output = temporaryDirectory.resolve("generic-contexts.jsonl");

        ContextExtractorMain.main(new String[] {
                "--projects-root", projects.toString(),
                "--subjects", subjects.toString(),
                "--output", output.toString()
        });

        JsonNode context = mapper.readTree(Files.readString(output).trim());
        JsonNode called = context.path("directlyCalledProjectMethods").get(0);
        assertTrue(called.path("focalClass").asText().equals("domain.EventWrapper"),
                context.toPrettyString());
        assertTrue(called.path("source").asText().contains("return event"), context.toPrettyString());
        JsonNode related = context.path("relatedTypeApis").get(0);
        assertTrue(toTextValues(related.path("accessibleConstructorSources")).stream()
                .anyMatch(value -> value.contains("this(null)")), context.toPrettyString());
    }

    private static void assertExcluded(
            List<JsonNode> candidates, String focalClassSuffix, String method, String reason) {
        JsonNode candidate = find(candidates, focalClassSuffix, method);
        assertFalse(candidate.path("eligible").asBoolean(), candidate.toPrettyString());
        assertTrue(toTextValues(candidate.path("exclusionReasons")).contains(reason),
                candidate.toPrettyString());
    }

    private static List<String> toTextValues(JsonNode array) {
        List<String> values = new ArrayList<>();
        array.forEach(value -> values.add(value.asText()));
        return values;
    }

    private static List<String> toTextValues(JsonNode array, String property) {
        List<String> values = new ArrayList<>();
        array.forEach(value -> values.add(value.path(property).asText()));
        return values;
    }

    private static JsonNode find(
            List<JsonNode> candidates, String focalClassSuffix, String method) {
        return candidates.stream()
                .filter(candidate -> candidate.path("focalClass").asText().endsWith(focalClassSuffix))
                .filter(candidate -> candidate.path("focalMethodSignature").asText().equals(method))
                .findFirst()
                .orElseThrow(() -> new AssertionError(
                        "Missing candidate " + focalClassSuffix + "." + method));
    }
}
