#!/usr/bin/env bash
set -euo pipefail

workspace_root="${1:-$(pwd)}"
benchmark_root="$workspace_root/datasets/rq1/projecttest/ProjectTest"
source_root="$benchmark_root/dataset/Java"
pom_root="$benchmark_root/javatest/poms/abcd_without"
output_root="$workspace_root/datasets/rq1/projecttest/java-ready"

if [[ ! -d "$source_root" || ! -d "$pom_root" ]]; then
  echo "ProjectTest checkout is incomplete: $benchmark_root" >&2
  exit 2
fi

if [[ -e "$output_root" ]]; then
  echo "Refusing to overwrite existing derived dataset: $output_root" >&2
  exit 3
fi

mkdir -p "$output_root/projects"

project_count=0
java_file_count=0
for source_project in "$source_root"/*; do
  [[ -d "$source_project" ]] || continue
  project_id="$(basename "$source_project")"
  official_pom="$pom_root/o1_${project_id}/pom.xml"
  if [[ ! -f "$official_pom" ]]; then
    echo "Missing official Maven harness for $project_id" >&2
    exit 4
  fi

  project_output="$output_root/projects/$project_id"
  java_output="$project_output/src/main/java/projecttest/$project_id"
  mkdir -p "$java_output"
  cp "$official_pom" "$project_output/pom.xml"
  cp -R "$source_project"/. "$java_output"/

  project_count=$((project_count + 1))
  project_java_count="$(find "$source_project" -type f -name '*.java' | wc -l | tr -d ' ')"
  java_file_count=$((java_file_count + project_java_count))
done

revision="$(git -C "$benchmark_root" rev-parse HEAD)"
printf '%s\n' "$revision" > "$output_root/REVISION"
(cd "$benchmark_root" && find dataset/Java -type f -name '*.java' | LC_ALL=C sort | xargs shasum -a 256) \
  > "$output_root/upstream-source-sha256.txt"
{
  printf 'benchmark,revision,language,project_count,java_file_count\n'
  printf 'ProjectTest,%s,Java,%s,%s\n' "$revision" "$project_count" "$java_file_count"
} > "$output_root/dataset-summary.csv"

{
  printf 'project_id,repository_path,java_file_count,pom_source\n'
  for project_output in "$output_root"/projects/*; do
    project_id="$(basename "$project_output")"
    count="$(find "$project_output/src/main/java" -type f -name '*.java' | wc -l | tr -d ' ')"
    printf '%s,%s,%s,%s\n' \
      "$project_id" \
      "projects/$project_id" \
      "$count" \
      "javatest/poms/abcd_without/o1_${project_id}/pom.xml"
  done
} > "$output_root/projects.csv"

echo "Prepared $project_count ProjectTest Java projects ($java_file_count Java files) at $output_root"
