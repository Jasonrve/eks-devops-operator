import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'

// DOCS_BASE is set by the docs workflow (e.g. /eks-devops-operator/ for GitHub Pages project sites).
export default withMermaid(defineConfig({
  title: 'EKS DevOps Operator',
  description: 'AWS access for Kubernetes workloads on EKS from one custom resource: IAM role, Pod Identity association, guardrails and tags.',
  base: process.env.DOCS_BASE ?? '/',
  cleanUrls: true,
  lastUpdated: true,
  head: [['link', { rel: 'icon', type: 'image/svg+xml', href: `${process.env.DOCS_BASE ?? '/'}logo.svg` }]],
  themeConfig: {
    logo: '/logo.svg',
    nav: [
      { text: 'Guide', link: '/guide/getting-started' },
      { text: 'Reference', link: '/reference/podidentityaccess' },
      { text: 'Security', link: '/guide/security' },
    ],
    sidebar: [
      {
        text: 'Introduction',
        items: [
          { text: 'What it does', link: '/' },
          { text: 'How it works', link: '/guide/how-it-works' },
        ],
      },
      {
        text: 'Guide',
        items: [
          { text: 'Getting started', link: '/guide/getting-started' },
          { text: 'AWS setup', link: '/guide/aws-setup' },
          { text: 'Installing with Helm', link: '/guide/installation' },
          { text: 'Configuration', link: '/guide/configuration' },
          { text: 'IRSA', link: '/guide/irsa' },
          { text: 'ServiceAccount annotations', link: '/guide/annotations' },
          { text: 'Security model', link: '/guide/security' },
          { text: 'Image and sizing', link: '/guide/sizing' },
          { text: 'Observability', link: '/guide/observability' },
          { text: 'Troubleshooting', link: '/guide/troubleshooting' },
        ],
      },
      {
        text: 'Reference',
        items: [
          { text: 'PodIdentityAccess', link: '/reference/podidentityaccess' },
          { text: 'IrsaAccess', link: '/reference/irsaaccess' },
          { text: 'PodIdentityPolicy', link: '/reference/podidentitypolicy' },
          { text: 'Helm values', link: '/reference/helm-values' },
          { text: 'Validation errors', link: '/reference/validation-errors' },
        ],
      },
      {
        text: 'Contributing',
        items: [{ text: 'Development and testing', link: '/guide/development' }],
      },
    ],
    search: { provider: 'local' },
    editLink: { pattern: 'https://github.com/jasonrve/eks-devops-operator/edit/main/docs/:path' },
    socialLinks: [{ icon: 'github', link: 'https://github.com/jasonrve/eks-devops-operator' }],
    footer: { message: 'Released under the MIT License.' },
  },
}))
