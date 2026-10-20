"""Create a combined source archive using an explicit allowlist, never local caches."""
from pathlib import Path
import zipfile


def main():
    root = Path(__file__).resolve().parent.parent
    output = root / "windows" / "artifacts" / "LinkLab-1.0-all-platforms-source.zip"
    output.parent.mkdir(parents=True, exist_ok=True)
    files = [root / name for name in (".gitignore", ".gitattributes", "LICENSE", "README.md", "README.en.md")]
    files += list((root / "scripts").glob("*.py"))
    for platform in ("windows", "linux_ubuntu"):
        project = root / platform
        for name in ("LICENSE", "README.md", "README.en.md", "THIRD-PARTY-NOTICES.md", "pyproject.toml", "requirements.txt", "main.py", "Directory.Build.props", "global.json", "LinkLab.sln", "NuGet.Config"):
            if (project / name).is_file():
                files.append(project / name)
        for directory in ("src", "tests", "scripts", "docs", "licenses"):
            folder = project / directory
            if folder.is_dir():
                files += [path for path in folder.rglob("*") if path.is_file() and not any(part.lower() in ("bin", "obj", "__pycache__", ".venv", ".build", "build", "dist", "artifacts") or part.lower().endswith(".egg-info") for part in path.relative_to(folder).parts)]
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(set(files)):
            if path.is_file() and path.suffix.lower() not in (".pfx", ".p12", ".key", ".log", ".tmp", ".bak") and not path.name.startswith(".env"):
                archive.write(path, "LinkLab/"+path.relative_to(root).as_posix())
    print(output)


if __name__ == "__main__":
    main()
