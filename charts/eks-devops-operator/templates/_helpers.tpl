{{- define "pio.selector" -}}
app.kubernetes.io/name: eks-devops-operator
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "pio.labels" -}}
{{ include "pio.selector" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end }}
