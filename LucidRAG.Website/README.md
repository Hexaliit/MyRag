# LucidRAG Website

Product website for LucidRAG - an open-source agentic RAG platform.

## Features

- Professional corporate design
- Responsive layout (mobile/tablet/desktop)
- TailwindCSS + DaisyUI styling
- Alpine.js for interactivity
- Static site with Vite

## Pages

- **Home** - Landing page with features overview
- **Features** - Deep dive into all features
- **Pricing** - Flexible pricing with feature selector
- **Architecture** - System architecture documentation
- **Documentation** - Quick start guides
- **Comparison** - Side-by-side plan comparison
- **About** - Project information and roadmap

## Development

```bash
# Install dependencies
npm install

# Build CSS
npm run build:css

# Development server
npm run dev

# Production build
npm run build

# Preview production build
npm run preview
```

## Project Structure

```
LucidRAG.Website/
├── src/                 # Source files
│   ├── css/            # Tailwind CSS
│   ├── js/             # Alpine.js components
│   ├── images/         # Placeholder images
│   ├── index.html       # Home page
│   ├── features.html    # Features page
│   ├── pricing.html     # Pricing page
│   ├── architecture.html # Architecture page
│   ├── docs.html       # Documentation page
│   ├── comparison.html  # Comparison page
│   └── about.html      # About page
├── dist/               # Production build output
├── package.json
├── vite.config.js      # Vite configuration
├── tailwind.config.js   # Tailwind configuration
└── postcss.config.cjs  # PostCSS configuration
```

## Tech Stack

- **Build System**: Vite
- **Styling**: TailwindCSS + DaisyUI
- **Interactivity**: Alpine.js
- **Fonts**: Raleway (headings), Inter (body)
- **Icons**: Inline SVG (Lucide-style)

## Deployment

The site builds to the `dist/` folder and can be deployed to:
- GitHub Pages
- Netlify
- Vercel
- Any static file hosting

Simply upload the contents of the `dist/` folder to your hosting service.
