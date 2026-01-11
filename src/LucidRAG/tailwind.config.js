/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    './Views/**/*.cshtml',
    './wwwroot/**/*.html',
    './wwwroot/**/*.js',
    './src/js/**/*.js'
  ],
  theme: {
    extend: {
      fontFamily: {
        'brand': ['Raleway', 'sans-serif']
      }
    }
  },
  plugins: [
    require('daisyui'),
    require('@tailwindcss/forms'),
    require('@tailwindcss/typography')
  ],
  daisyui: {
    themes: [
      'light',
      'dark',
      'cupcake',      // Soft, friendly pastels
      'corporate',    // Professional, clean
      'synthwave',    // Retro neon
      'retro',        // Vintage feel
      'cyberpunk',    // Bold, futuristic
      'valentine',    // Pink/romantic
      'aqua',         // Cool blues
      'lofi',         // Muted, relaxed
      'pastel',       // Soft colors
      'dracula',      // Dark purple
      'night',        // Deep dark
      'coffee',       // Warm browns
      'nord',         // Arctic colors
      'sunset',       // Warm oranges
      // Custom ChatGPT-style theme
      {
        chatgpt: {
          'primary': '#10a37f',
          'primary-content': '#ffffff',
          'secondary': '#5436da',
          'secondary-content': '#ffffff',
          'accent': '#19c37d',
          'accent-content': '#ffffff',
          'neutral': '#343541',
          'neutral-content': '#ececf1',
          'base-100': '#ffffff',
          'base-200': '#f7f7f8',
          'base-300': '#ececf1',
          'base-content': '#343541',
          'info': '#0ea5e9',
          'success': '#10a37f',
          'warning': '#f59e0b',
          'error': '#ef4444',
          '--rounded-box': '0.5rem',
          '--rounded-btn': '0.375rem',
          '--rounded-badge': '1rem',
        }
      },
      // Fun playful theme
      {
        fun: {
          'primary': '#ff6b6b',
          'primary-content': '#ffffff',
          'secondary': '#4ecdc4',
          'secondary-content': '#ffffff',
          'accent': '#ffe66d',
          'accent-content': '#1a1a2e',
          'neutral': '#2d3436',
          'neutral-content': '#ffffff',
          'base-100': '#fff9f0',
          'base-200': '#ffe8d6',
          'base-300': '#ffd9b3',
          'base-content': '#2d3436',
          'info': '#74b9ff',
          'success': '#00b894',
          'warning': '#fdcb6e',
          'error': '#ff7675',
          '--rounded-box': '1.5rem',
          '--rounded-btn': '2rem',
          '--rounded-badge': '2rem',
        }
      },
      // Minimal/clean theme
      {
        minimal: {
          'primary': '#18181b',
          'primary-content': '#fafafa',
          'secondary': '#52525b',
          'secondary-content': '#fafafa',
          'accent': '#3b82f6',
          'accent-content': '#ffffff',
          'neutral': '#27272a',
          'neutral-content': '#fafafa',
          'base-100': '#ffffff',
          'base-200': '#fafafa',
          'base-300': '#f4f4f5',
          'base-content': '#18181b',
          'info': '#3b82f6',
          'success': '#22c55e',
          'warning': '#eab308',
          'error': '#ef4444',
          '--rounded-box': '0.25rem',
          '--rounded-btn': '0.25rem',
          '--rounded-badge': '0.25rem',
        }
      }
    ],
    darkTheme: 'dark'
  }
}
