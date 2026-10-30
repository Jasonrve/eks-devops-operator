# Contributing

Issues and pull requests are welcome.

1. Fork, create a branch, make your change.
2. Run the tests (they use the Floci AWS emulator, so no AWS account is needed). See [docs/guide/development.md](docs/guide/development.md).
3. If you change a CRD entity, regenerate the chart CRDs (`dotnet run --project src/EksDevopsOperator -- generate-crds "$PWD/deploy/helm/eks-devops-operator/crds"`); CI fails if they are out of date.
4. Open a pull request describing what and why.

Security problems: do not open an issue, see [SECURITY.md](SECURITY.md).
By contributing you agree your work is released under the [MIT License](LICENSE).
