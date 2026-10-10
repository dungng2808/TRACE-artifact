# RQ1 tools

These scripts and `java-structural-analyzer/` were relocated from the wider workspace's `tools/` directory. Run commands from the repository root, for example:

```sh
python3 tools/rq1/prepare_sf110_java.py
python3 tools/rq1/validate_sf110_java.py
bash tools/rq1/prepare_projecttest_java.sh
```

These are separate preparation and validation steps. Read each script before running it, and check that its required dataset and toolchain are available. Some historical scripts directly modify experiment outputs; their inclusion here does not establish them as the official result-reproduction workflow.
