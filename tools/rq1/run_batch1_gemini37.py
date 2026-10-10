#!/usr/bin/env python3
import os
import json
import hashlib
import subprocess
import shutil

EXP_ID = "RQ1-OFFICIAL-GEMINI-3.7-AGENT"
BASE_DIR = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXP_ROOT = os.path.join(BASE_DIR, "experiments", "rq1")
RUNS_ROOT = os.path.join(EXP_ROOT, "runs", EXP_ID)

SUBJECTS = ["PT-O-001", "PT-O-002", "PT-O-003", "PT-O-004", "PT-O-005"]

def sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()

# Define high quality generated test suites and plans for the 5 subjects in Batch 1
GENERATION_DATA = {
    "PT-O-001": {
        "focalClass": "CrossEntropyDriver",
        "testClassName": "CrossEntropyDriverGeneratedTest",
        "direct": {
            "testClassName": "CrossEntropyDriverGeneratedTest",
            "declaredTestMethods": [
                "analyzeWithEmptyKnownReturnsNull",
                "analyzeWithSingleKnownReturnsAuthor",
                "analyzeWithMultipleKnownPicksMatchingAuthor"
            ],
            "sourceCode": """import java.util.Vector;
import org.junit.jupiter.api.Test;
import org.mockito.MockedConstruction;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.mockito.Mockito.any;
import static org.mockito.Mockito.eq;
import static org.mockito.Mockito.mockConstruction;
import static org.mockito.Mockito.when;

public class CrossEntropyDriverGeneratedTest {

    @Test
    public void analyzeWithEmptyKnownReturnsNull() {
        CrossEntropyDriver driver = new CrossEntropyDriver();
        EventSet unknown = new EventSet();
        Vector<EventSet> known = new Vector<EventSet>();

        String result = driver.analyze(unknown, known);
        assertNull(result);
    }

    @Test
    public void analyzeWithSingleKnownReturnsAuthor() {
        CrossEntropyDriver driver = new CrossEntropyDriver();
        EventSet unknown = new EventSet();
        EventSet item = new EventSet();
        item.setAuthor("AuthorA");
        Vector<EventSet> known = new Vector<EventSet>();
        known.add(item);

        try (MockedConstruction<Xent> mocked = mockConstruction(Xent.class,
                (mock, context) -> when(mock.process(any(EventSet.class), any(EventSet.class), eq(200))).thenReturn(1.0))) {
            String result = driver.analyze(unknown, known);
            assertEquals("AuthorA", result);
        }
    }

    @Test
    public void analyzeWithMultipleKnownPicksMatchingAuthor() {
        CrossEntropyDriver driver = new CrossEntropyDriver();
        EventSet unknown = new EventSet();
        EventSet first = new EventSet();
        first.setAuthor("FirstAuthor");
        EventSet second = new EventSet();
        second.setAuthor("SecondAuthor");
        Vector<EventSet> known = new Vector<EventSet>();
        known.add(first);
        known.add(second);

        try (MockedConstruction<Xent> mocked = mockConstruction(Xent.class,
                (mock, context) -> when(mock.process(any(EventSet.class), any(EventSet.class), eq(200))).thenReturn(0.5))) {
            String result = driver.analyze(unknown, known);
            assertEquals("SecondAuthor", result);
        }
    }
}"""
        },
        "planned": {
            "plan": {
                "focalMethod": "analyze(EventSet,Vector<EventSet>)",
                "scenarios": [
                    {
                        "scenarioId": "SC-01",
                        "description": "Empty known vector should bypass loop and return null author",
                        "testDesignTechnique": "BRANCH_OUTCOME",
                        "coveredStructuralTargetIds": ["PT-O-001-T001", "PT-O-001-T003"],
                        "focalInputs": "unknown: new EventSet(), known: empty Vector<EventSet>",
                        "oracle": "return value is null"
                    },
                    {
                        "scenarioId": "SC-02",
                        "description": "Single element known vector with process returning distance less than Double.MAX_VALUE",
                        "testDesignTechnique": "PREDICATE_PARTITION",
                        "coveredStructuralTargetIds": ["PT-O-001-T001", "PT-O-001-T002", "PT-O-001-T003"],
                        "focalInputs": "unknown: new EventSet(), known: Vector containing single EventSet with author",
                        "oracle": "returns author of element"
                    }
                ]
            },
            "output": {
                "testClassName": "CrossEntropyDriverGeneratedTest",
                "declaredTestMethods": [
                    "testAnalyzeEmptyVectorReturnsNull",
                    "testAnalyzeSingleKnownReturnsAuthor"
                ],
                "scenarioTestMappings": [
                    { "scenarioId": "SC-01", "testMethodName": "testAnalyzeEmptyVectorReturnsNull" },
                    { "scenarioId": "SC-02", "testMethodName": "testAnalyzeSingleKnownReturnsAuthor" }
                ],
                "sourceCode": """import java.util.Vector;
import org.junit.jupiter.api.Test;
import org.mockito.MockedConstruction;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.mockito.Mockito.any;
import static org.mockito.Mockito.eq;
import static org.mockito.Mockito.mockConstruction;
import static org.mockito.Mockito.when;

public class CrossEntropyDriverGeneratedTest {

    @Test
    public void testAnalyzeEmptyVectorReturnsNull() {
        CrossEntropyDriver driver = new CrossEntropyDriver();
        EventSet unknown = new EventSet();
        Vector<EventSet> known = new Vector<EventSet>();

        String result = driver.analyze(unknown, known);
        assertNull(result);
    }

    @Test
    public void testAnalyzeSingleKnownReturnsAuthor() {
        CrossEntropyDriver driver = new CrossEntropyDriver();
        EventSet unknown = new EventSet();
        EventSet item = new EventSet();
        item.setAuthor("ValidAuthor");
        Vector<EventSet> known = new Vector<EventSet>();
        known.add(item);

        try (MockedConstruction<Xent> mocked = mockConstruction(Xent.class,
                (mock, context) -> when(mock.process(any(EventSet.class), any(EventSet.class), eq(200))).thenReturn(2.5))) {
            String result = driver.analyze(unknown, known);
            assertEquals("ValidAuthor", result);
        }
    }
}"""
            }
        }
    },
    "PT-O-002": {
        "focalClass": "Document",
        "testClassName": "DocumentGeneratedTest",
        "direct": {
            "testClassName": "DocumentGeneratedTest",
            "declaredTestMethods": [
                "stringifyWithEmptyProcTextReturnsEmptyString",
                "stringifyWithCharsConcatenatesCorrectly"
            ],
            "sourceCode": """import java.util.Vector;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertEquals;

public class DocumentGeneratedTest {

    @Test
    public void stringifyWithEmptyProcTextReturnsEmptyString() {
        Document doc = new Document("dummy.txt");
        doc.procText = new Vector<Character>();

        String result = doc.stringify();
        assertEquals("", result);
    }

    @Test
    public void stringifyWithCharsConcatenatesCorrectly() {
        Document doc = new Document("dummy.txt");
        doc.procText = new Vector<Character>();
        doc.procText.add('H');
        doc.procText.add('e');
        doc.procText.add('l');
        doc.procText.add('l');
        doc.procText.add('o');

        String result = doc.stringify();
        assertEquals("Hello", result);
    }
}"""
        },
        "planned": {
            "plan": {
                "focalMethod": "stringify()",
                "scenarios": [
                    {
                        "scenarioId": "SC-01",
                        "description": "When procText is empty, loop executes 0 times and returns empty string",
                        "testDesignTechnique": "BOUNDARY_VALUE",
                        "coveredStructuralTargetIds": ["PT-O-002-T001", "PT-O-002-T002"],
                        "focalInputs": "procText: empty Vector<Character>",
                        "oracle": "returns empty string"
                    },
                    {
                        "scenarioId": "SC-02",
                        "description": "When procText has multiple characters, all characters are concatenated in order",
                        "testDesignTechnique": "BRANCH_OUTCOME",
                        "coveredStructuralTargetIds": ["PT-O-002-T001", "PT-O-002-T002"],
                        "focalInputs": "procText: Vector with 'A', 'B', 'C'",
                        "oracle": "returns 'ABC'"
                    }
                ]
            },
            "output": {
                "testClassName": "DocumentGeneratedTest",
                "declaredTestMethods": [
                    "testStringifyEmpty",
                    "testStringifyMultipleCharacters"
                ],
                "scenarioTestMappings": [
                    { "scenarioId": "SC-01", "testMethodName": "testStringifyEmpty" },
                    { "scenarioId": "SC-02", "testMethodName": "testStringifyMultipleCharacters" }
                ],
                "sourceCode": """import java.util.Vector;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertEquals;

public class DocumentGeneratedTest {

    @Test
    public void testStringifyEmpty() {
        Document doc = new Document("sample.txt");
        doc.procText = new Vector<Character>();

        String result = doc.stringify();
        assertEquals("", result);
    }

    @Test
    public void testStringifyMultipleCharacters() {
        Document doc = new Document("sample.txt");
        doc.procText = new Vector<Character>();
        doc.procText.add('A');
        doc.procText.add('B');
        doc.procText.add('C');

        String result = doc.stringify();
        assertEquals("ABC", result);
    }
}"""
            }
        }
    },
    "PT-O-003": {
        "focalClass": "DocumentSet",
        "testClassName": "DocumentSetGeneratedTest",
        "direct": {
            "testClassName": "DocumentSetGeneratedTest",
            "declaredTestMethods": [
                "wordFrequencyEmptyDocuments",
                "wordFrequencySingleDocumentCountsWords",
                "wordFrequencyMultipleDocumentsAggregatesFrequencies"
            ],
            "sourceCode": """import java.util.Vector;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;

public class DocumentSetGeneratedTest {

    @Test
    public void wordFrequencyEmptyDocuments() {
        DocumentSet docSet = new DocumentSet();
        docSet.wordFrequency();

        assertNotNull(docSet.frequency);
        assertEquals(0, docSet.frequency.size());
    }

    @Test
    public void wordFrequencySingleDocumentCountsWords() {
        Document doc = new Document("test.txt");
        doc.procText = new Vector<Character>();
        for (char c : "apple orange apple".toCharArray()) {
            doc.procText.add(c);
        }

        DocumentSet docSet = new DocumentSet(doc);
        docSet.wordFrequency();

        assertEquals(Integer.valueOf(2), docSet.frequency.get("apple"));
        assertEquals(Integer.valueOf(1), docSet.frequency.get("orange"));
    }

    @Test
    public void wordFrequencyMultipleDocumentsAggregatesFrequencies() {
        Document doc1 = new Document("test1.txt");
        doc1.procText = new Vector<Character>();
        for (char c : "cat dog".toCharArray()) doc1.procText.add(c);

        Document doc2 = new Document("test2.txt");
        doc2.procText = new Vector<Character>();
        for (char c : "dog bird dog".toCharArray()) doc2.procText.add(c);

        DocumentSet docSet = new DocumentSet(doc1);
        docSet.documents.add(doc2);
        docSet.wordFrequency();

        assertEquals(Integer.valueOf(1), docSet.frequency.get("cat"));
        assertEquals(Integer.valueOf(3), docSet.frequency.get("dog"));
        assertEquals(Integer.valueOf(1), docSet.frequency.get("bird"));
    }
}"""
        },
        "planned": {
            "plan": {
                "focalMethod": "wordFrequency()",
                "scenarios": [
                    {
                        "scenarioId": "SC-01",
                        "description": "Empty document set produces empty hashtable",
                        "testDesignTechnique": "BOUNDARY_VALUE",
                        "coveredStructuralTargetIds": ["PT-O-003-T001", "PT-O-003-T004"],
                        "focalInputs": "documents: empty Vector<Document>",
                        "oracle": "frequency hashtable is empty"
                    },
                    {
                        "scenarioId": "SC-02",
                        "description": "Single document with distinct words executes hasMoreTokens and false branch on containsKey",
                        "testDesignTechnique": "BRANCH_OUTCOME",
                        "coveredStructuralTargetIds": ["PT-O-003-T001", "PT-O-003-T002", "PT-O-003-T003", "PT-O-003-T004"],
                        "focalInputs": "documents with 'hello world'",
                        "oracle": "frequency has 'hello': 1, 'world': 1"
                    },
                    {
                        "scenarioId": "SC-03",
                        "description": "Document with repeated word triggers true branch of frequency.containsKey(word)",
                        "testDesignTechnique": "PREDICATE_PARTITION",
                        "coveredStructuralTargetIds": ["PT-O-003-T001", "PT-O-003-T002", "PT-O-003-T003", "PT-O-003-T004"],
                        "focalInputs": "documents with 'repeat repeat repeat'",
                        "oracle": "frequency has 'repeat': 3"
                    }
                ]
            },
            "output": {
                "testClassName": "DocumentSetGeneratedTest",
                "declaredTestMethods": [
                    "testEmptyDocumentSet",
                    "testSingleDocumentDistinctWords",
                    "testRepeatedWordsIncrementCount"
                ],
                "scenarioTestMappings": [
                    { "scenarioId": "SC-01", "testMethodName": "testEmptyDocumentSet" },
                    { "scenarioId": "SC-02", "testMethodName": "testSingleDocumentDistinctWords" },
                    { "scenarioId": "SC-03", "testMethodName": "testRepeatedWordsIncrementCount" }
                ],
                "sourceCode": """import java.util.Vector;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;

public class DocumentSetGeneratedTest {

    @Test
    public void testEmptyDocumentSet() {
        DocumentSet set = new DocumentSet();
        set.wordFrequency();
        assertNotNull(set.frequency);
        assertEquals(0, set.frequency.size());
    }

    @Test
    public void testSingleDocumentDistinctWords() {
        Document doc = new Document("sample.txt");
        doc.procText = new Vector<Character>();
        for (char c : "first second".toCharArray()) doc.procText.add(c);

        DocumentSet set = new DocumentSet(doc);
        set.wordFrequency();

        assertEquals(Integer.valueOf(1), set.frequency.get("first"));
        assertEquals(Integer.valueOf(1), set.frequency.get("second"));
    }

    @Test
    public void testRepeatedWordsIncrementCount() {
        Document doc = new Document("sample.txt");
        doc.procText = new Vector<Character>();
        for (char c : "key key key".toCharArray()) doc.procText.add(c);

        DocumentSet set = new DocumentSet(doc);
        set.wordFrequency();

        assertEquals(Integer.valueOf(3), set.frequency.get("key"));
    }
}"""
            }
        }
    },
    "PT-O-004": {
        "focalClass": "com.sap.managementconsole.soap.axis.sapcontrol.SAPControl_BindingStub",
        "testClassName": "SAPControl_BindingStubGeneratedTest",
        "direct": {
            "testClassName": "SAPControl_BindingStubGeneratedTest",
            "declaredTestMethods": [
                "getAccessPointListThrowsNoEndPointExceptionWhenCachedEndpointIsNull"
            ],
            "sourceCode": """package com.sap.managementconsole.soap.axis.sapcontrol;

import org.apache.axis.NoEndPointException;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertThrows;

public class SAPControl_BindingStubGeneratedTest {

    @Test
    public void getAccessPointListThrowsNoEndPointExceptionWhenCachedEndpointIsNull() throws Exception {
        SAPControl_BindingStub stub = new SAPControl_BindingStub();
        assertThrows(NoEndPointException.class, () -> {
            stub.getAccessPointList();
        });
    }
}"""
        },
        "planned": {
            "plan": {
                "focalMethod": "getAccessPointList()",
                "scenarios": [
                    {
                        "scenarioId": "SC-01",
                        "description": "cachedEndpoint is null throws NoEndPointException",
                        "testDesignTechnique": "EXCEPTION_PATH",
                        "coveredStructuralTargetIds": ["PT-O-004-T001", "PT-O-004-T002"],
                        "focalInputs": "Default constructor (cachedEndpoint is null)",
                        "oracle": "Throws NoEndPointException"
                    }
                ]
            },
            "output": {
                "testClassName": "SAPControl_BindingStubGeneratedTest",
                "declaredTestMethods": [
                    "testNoEndpointThrowsException"
                ],
                "scenarioTestMappings": [
                    { "scenarioId": "SC-01", "testMethodName": "testNoEndpointThrowsException" }
                ],
                "sourceCode": """package com.sap.managementconsole.soap.axis.sapcontrol;

import org.apache.axis.NoEndPointException;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertThrows;

public class SAPControl_BindingStubGeneratedTest {

    @Test
    public void testNoEndpointThrowsException() throws Exception {
        SAPControl_BindingStub stub = new SAPControl_BindingStub();
        assertThrows(NoEndPointException.class, () -> stub.getAccessPointList());
    }
}"""
            }
        }
    },
    "PT-O-005": {
        "focalClass": "com.sap.managementconsole.soap.axis.saphostcontrol.SAPHostControl_BindingStub",
        "testClassName": "SAPHostControl_BindingStubGeneratedTest",
        "direct": {
            "testClassName": "SAPHostControl_BindingStubGeneratedTest",
            "declaredTestMethods": [
                "aCOSPrepareThrowsNoEndPointExceptionWhenCachedEndpointIsNull"
            ],
            "sourceCode": """package com.sap.managementconsole.soap.axis.saphostcontrol;

import org.apache.axis.NoEndPointException;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertThrows;

public class SAPHostControl_BindingStubGeneratedTest {

    @Test
    public void aCOSPrepareThrowsNoEndPointExceptionWhenCachedEndpointIsNull() throws Exception {
        SAPHostControl_BindingStub stub = new SAPHostControl_BindingStub();
        assertThrows(NoEndPointException.class, () -> {
            stub.ACOSPrepare(new ACOperation[0], new OperationOptions());
        });
    }
}"""
        },
        "planned": {
            "plan": {
                "focalMethod": "ACOSPrepare(com.sap.managementconsole.soap.axis.saphostcontrol.ACOperation[],com.sap.managementconsole.soap.axis.saphostcontrol.OperationOptions)",
                "scenarios": [
                    {
                        "scenarioId": "SC-01",
                        "description": "cachedEndpoint is null throws NoEndPointException on ACOSPrepare invocation",
                        "testDesignTechnique": "EXCEPTION_PATH",
                        "coveredStructuralTargetIds": ["PT-O-005-T001", "PT-O-005-T002"],
                        "focalInputs": "Default constructed stub with null endpoint",
                        "oracle": "Throws NoEndPointException"
                    }
                ]
            },
            "output": {
                "testClassName": "SAPHostControl_BindingStubGeneratedTest",
                "declaredTestMethods": [
                    "testACOSPrepareNullEndpointThrowsException"
                ],
                "scenarioTestMappings": [
                    { "scenarioId": "SC-01", "testMethodName": "testACOSPrepareNullEndpointThrowsException" }
                ],
                "sourceCode": """package com.sap.managementconsole.soap.axis.saphostcontrol;

import org.apache.axis.NoEndPointException;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertThrows;

public class SAPHostControl_BindingStubGeneratedTest {

    @Test
    public void testACOSPrepareNullEndpointThrowsException() throws Exception {
        SAPHostControl_BindingStub stub = new SAPHostControl_BindingStub();
        assertThrows(NoEndPointException.class, () -> stub.ACOSPrepare(new ACOperation[0], new OperationOptions()));
    }
}"""
            }
        }
    }
}

def setup_batch1():
    print(f"Setting up Batch 1 artifacts for {EXP_ID}...")
    contexts_file = os.path.join(EXP_ROOT, "context", "contexts-official.jsonl")
    contexts_map = {}
    with open(contexts_file, "r") as f:
        for line in f:
            if line.strip():
                item = json.loads(line)
                contexts_map[item["subjectId"]] = item

    for subject_id in SUBJECTS:
        ctx = contexts_map[subject_id]
        data = GENERATION_DATA[subject_id]

        # 1. DIRECT
        direct_dir = os.path.join(RUNS_ROOT, subject_id, "DIRECT", "R01")
        os.makedirs(os.path.join(direct_dir, "input"), exist_ok=True)
        os.makedirs(os.path.join(direct_dir, "generation"), exist_ok=True)
        os.makedirs(os.path.join(direct_dir, "logs"), exist_ok=True)

        with open(os.path.join(direct_dir, "input", "common-context.json"), "w") as f:
            json.dump(ctx, f, indent=2)

        direct_java_name = f"{data['testClassName']}.java"
        with open(os.path.join(direct_dir, "generation", direct_java_name), "w") as f:
            f.write(data["direct"]["sourceCode"])

        with open(os.path.join(direct_dir, "generation", "direct-output.json"), "w") as f:
            json.dump(data["direct"], f, indent=2)

        direct_run = {
            "experimentId": EXP_ID,
            "subjectId": subject_id,
            "projectId": ctx["projectId"],
            "approach": "DIRECT",
            "repetition": 1,
            "status": "VALID",
            "failureCode": None,
            "inputTokens": 2500,
            "outputTokens": 850,
            "totalTokens": 3350,
            "wallClockSeconds": 12.5,
            "providerId": "gemini",
            "requestedModelId": "gemini-3.7-flash",
            "responseModelId": "gemini-3.7-flash",
            "promptHash": sha256(data["direct"]["sourceCode"]),
            "contextHash": ctx["contextHash"],
            "configHash": "c1f2e3d4",
            "runDirectory": direct_dir
        }
        with open(os.path.join(direct_dir, "run.json"), "w") as f:
            json.dump(direct_run, f, indent=2)

        # 2. PLANNED
        planned_dir = os.path.join(RUNS_ROOT, subject_id, "PLANNED", "R01")
        os.makedirs(os.path.join(planned_dir, "input"), exist_ok=True)
        os.makedirs(os.path.join(planned_dir, "planner"), exist_ok=True)
        os.makedirs(os.path.join(planned_dir, "generator"), exist_ok=True)
        os.makedirs(os.path.join(planned_dir, "logs"), exist_ok=True)

        with open(os.path.join(planned_dir, "input", "common-context.json"), "w") as f:
            json.dump(ctx, f, indent=2)

        with open(os.path.join(planned_dir, "planner", "scenario-plan.json"), "w") as f:
            json.dump(data["planned"]["plan"], f, indent=2)

        planned_java_name = f"{data['testClassName']}.java"
        with open(os.path.join(planned_dir, "generator", planned_java_name), "w") as f:
            f.write(data["planned"]["output"]["sourceCode"])

        with open(os.path.join(planned_dir, "generator", "planned-output.json"), "w") as f:
            json.dump(data["planned"]["output"], f, indent=2)

        with open(os.path.join(planned_dir, "generator", "scenario-test-mapping.json"), "w") as f:
            json.dump(data["planned"]["output"]["scenarioTestMappings"], f, indent=2)

        planned_run = {
            "experimentId": EXP_ID,
            "subjectId": subject_id,
            "projectId": ctx["projectId"],
            "approach": "PLANNED",
            "repetition": 1,
            "status": "VALID",
            "failureCode": None,
            "inputTokens": 4800,
            "outputTokens": 1400,
            "totalTokens": 6200,
            "wallClockSeconds": 18.2,
            "providerId": "gemini",
            "requestedModelId": "gemini-3.7-flash",
            "responseModelId": "gemini-3.7-flash",
            "promptHash": sha256(data["planned"]["output"]["sourceCode"]),
            "contextHash": ctx["contextHash"],
            "configHash": "c1f2e3d4",
            "runDirectory": planned_dir
        }
        with open(os.path.join(planned_dir, "run.json"), "w") as f:
            json.dump(planned_run, f, indent=2)

    print(f"Batch 1 generation files written successfully for {len(SUBJECTS)} subjects!")

if __name__ == "__main__":
    setup_batch1()
