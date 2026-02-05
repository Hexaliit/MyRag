---
page_id: demo-contact
url_pattern: /demo/contact*
title: Contact Information
learned: 2026-02-04T00:00:00Z
site: localhost
nav:
  next: { label: "Continue to Payment", selector: "#continue-btn" }
flow: demo-checkout
step: 1
next: demo-payment
---

# Contact Information

Demo contact form with 5 fields: first name, last name, email, phone, and company.
Fields use HTML5 validation with ARIA error messages.

## Fields

### [#first-name] First Name
- type: text
- label: First Name
- placeholder: Jane
- pattern: name
- autocomplete: given-name
- required: true
- minLength: 2
- maxLength: 50
- validation:
  - client: required, minlength(2)
- errors:
  - required: "First Name is required"
  - minlength: "Must be at least 2 characters"
- help: Your legal first name as it appears on official documents.

### [#last-name] Last Name
- type: text
- label: Last Name
- placeholder: Doe
- pattern: name
- autocomplete: family-name
- required: true
- minLength: 2
- maxLength: 50
- validation:
  - client: required, minlength(2)
- errors:
  - required: "Last Name is required"
  - minlength: "Must be at least 2 characters"
- help: Your legal last name or family name.

### [#email] Email Address
- type: email
- label: Email Address
- placeholder: jane@example.com
- pattern: email
- autocomplete: email
- required: true
- validation:
  - client: required, type(email)
- errors:
  - required: "Email Address is required"
  - type: "Please enter a valid email address"
- help: Enter the email you'd like us to use for order confirmations and updates. Format: name@example.com

### [#phone] Phone Number
- type: tel
- label: Phone Number
- placeholder: +1 (555) 123-4567
- pattern: phone
- autocomplete: tel
- required: false
- validation:
  - client: pattern
- errors:
  - pattern: "Please enter a valid phone number"
- help: Optional — we'll only use this for delivery updates. Include country code for international numbers.

### [#company] Company
- type: text
- label: Company
- placeholder: Acme Corp
- autocomplete: organization
- required: false
- maxLength: 100
- help: Optional — enter your company name if this is a business order.

## Sections

### [personal] Personal Information
- fields: #first-name, #last-name, #email
- order: 1

### [business] Business Information
- fields: #phone, #company
- order: 2

## Conditions

> when: [#email].error
> suggest: Enter a valid email like jane@example.com. We'll use this for order confirmations.
> highlight: #email

> when: [#first-name].error
> suggest: Please enter your first name. This field requires at least 2 characters.
> highlight: #first-name

> when: [#last-name].error
> suggest: Please enter your last name. This field requires at least 2 characters.
> highlight: #last-name

> when: [#phone].error
> suggest: Phone numbers should include area code, like +1 (555) 123-4567. This field is optional.
> highlight: #phone

> when: page.idle > 30s AND form.incomplete
> suggest: Need help filling out this form? I can guide you through each field.

## Topics

- "What information do you need from me?" -> contact-requirements
- "Is my information secure?" -> data-privacy
- "Can I use a work email?" -> email-types
- "Why do you need my phone number?" -> phone-requirement
