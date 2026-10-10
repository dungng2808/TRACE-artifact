import subprocess
prompt = "Generate a java class"
p = subprocess.run(["agy", "-p", prompt, "--model", "gemini-3.1-pro", "--effort", "high"], capture_output=True, text=True)
print("Return code:", p.returncode)
print("STDOUT:", p.stdout)
print("STDERR:", p.stderr)
