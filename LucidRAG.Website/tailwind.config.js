/** @type {import('tailwindcss').Config} */
module.exports = {
    content: [
        './src/**/*.html',
        './src/**/*.js'
    ],
    theme: {
        extend: {
            fontFamily: {
                'brand': ['Raleway', 'sans-serif'],
                'body': ['Inter', 'system-ui', 'sans-serif']
            },
            animation: {
                'fade-in': 'fadeIn 0.5s ease-in-out',
                'slide-up': 'slideUp 0.5s ease-out',
                'pulse-slow': 'pulse 3s ease-in-out infinite'
            },
            keyframes: {
                fadeIn: {
                    '0%': {opacity: '0'},
                    '100%': {opacity: '1'}
                },
                slideUp: {
                    '0%': {transform: 'translateY(20px)', opacity: '0'},
                    '100%': {transform: 'translateY(0)', opacity: '1'}
                }
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
            'corporate',
            'minimal',
            {
                lucidrag: {
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
            }
        ],
        darkTheme: 'dark'
    }
}
